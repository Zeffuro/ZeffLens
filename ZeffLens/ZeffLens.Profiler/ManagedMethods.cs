using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Parsers.Clr;
using ZeffLens.Diagnostics;

namespace ZeffLens.Profiler;

internal sealed class ManagedMethods(ManagedMethods.Entry[] methods, int resolvedAssemblies, int resolvedMethods,
    int resolvedPlugins, int ambiguousAssemblies)
{
    public int ResolvedAssemblies { get; } = resolvedAssemblies;
    public int ResolvedMethods { get; } = resolvedMethods;
    public int ResolvedPlugins { get; } = resolvedPlugins;
    public int AmbiguousAssemblies { get; } = ambiguousAssemblies;

    public static ManagedMethods Read(string trace, ProfileAssembly[] selected)
    {
        using var source = new EventPipeEventSource(trace);
        var assemblies = new Dictionary<long, string>();
        var modules = new Dictionary<long, long>();
        var unloadedModules = new HashSet<long>();
        var ranges = new Dictionary<ulong, Range>();
        var exact = selected.Where(item => item.ModuleId != 0).ToLookup(item => item.ModuleId);
        var fallback = selected.Where(item => item.ModuleId == 0).ToLookup(item => item.Identity, StringComparer.OrdinalIgnoreCase);
        var rundown = new ClrRundownTraceEventParser(source);
        source.Clr.LoaderAssemblyLoad += Assembly;
        rundown.LoaderAssemblyDCStop += Assembly;
        source.Clr.LoaderModuleLoad += Module;
        source.Clr.LoaderModuleUnload += data => unloadedModules.Add(data.ModuleID);
        rundown.LoaderModuleDCStop += Module;
        source.Clr.MethodLoadVerbose += Method;
        rundown.MethodDCStopVerbose += Method;
        source.Process();

        var duplicates = assemblies.Values.GroupBy(name => name, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1).Select(group => group.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var selectedModules = new Dictionary<long, (ProfileAssembly Assembly, string Label)>();
        foreach (var (moduleId, assemblyId) in modules)
        {
            if (unloadedModules.Contains(moduleId) || !assemblies.TryGetValue(assemblyId, out var identity))
                continue;

            var matches = exact[moduleId];
            if (!matches.Any() && !duplicates.Contains(identity))
                matches = fallback[identity];
            if (matches.Count() == 1 && matches.First().Identity.Equals(identity, StringComparison.OrdinalIgnoreCase))
                selectedModules[moduleId] = (matches.First(), identity.Split(',')[0]);
        }

        var resolved = new HashSet<ProfileAssembly>();
        var resolvedCount = 0;
        ulong earlierEnd = 0;
        var entries = ranges.Values.OrderBy(range => range.Start).Select(range =>
        {
            var previousEnd = earlierEnd;
            earlierEnd = Math.Max(earlierEnd, range.End);
            if (range.Ambiguous || previousEnd >= range.End || range.Method is null ||
                !selectedModules.TryGetValue(range.ModuleId, out var target))
                return new Entry(range.Start, range.End, previousEnd, null, null, null);
            resolved.Add(target.Assembly);
            resolvedCount++;
            return new Entry(range.Start, range.End, previousEnd, target.Assembly.Plugin, target.Label, range.Method);
        }).ToArray();
        return new ManagedMethods(entries, resolved.Count, resolvedCount,
            resolved.Select(item => item.Plugin).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
            fallback.Count(group => duplicates.Contains(group.Key) || group.Count() > 1));

        void Assembly(AssemblyLoadUnloadTraceData data) => assemblies[data.AssemblyID] = data.FullyQualifiedAssemblyName;
        void Module(ModuleLoadUnloadTraceData data) => modules[data.ModuleID] = data.AssemblyID;
        void Method(MethodLoadUnloadVerboseTraceData data)
        {
            var start = data.MethodStartAddress;
            if (start == 0 || data.MethodSize <= 0)
                return;
            var end = start + (uint)data.MethodSize;
            if (ranges.TryGetValue(start, out var previous) &&
                (previous.ModuleId != data.ModuleID || previous.End != end))
            {
                ranges[start] = previous with { End = Math.Max(end, previous.End), Ambiguous = true };
                return;
            }

            if (!previous.Ambiguous)
            {
                if (!exact.Contains(data.ModuleID) && fallback.Count == 0)
                {
                    ranges[start] = new Range(start, end, data.ModuleID, null, false);
                    return;
                }
                var signature = data.MethodSignature;
                var parameters = signature.IndexOf('(');
                var name = $"{data.MethodNamespace}.{data.MethodName}";
                if (parameters >= 0)
                    name += signature[parameters..];
                ranges[start] = new Range(start, end, data.ModuleID, name, false);
            }
        }
    }

    public Entry? Find(ulong address)
    {
        var low = 0;
        var high = methods.Length;
        while (low < high)
        {
            var mid = low + (high - low) / 2;
            if (methods[mid].Start <= address)
                low = mid + 1;
            else
                high = mid;
        }

        if (low == 0)
            return null;
        var entry = methods[low - 1];
        return entry.Module is not null && address < entry.End && entry.EarlierEnd <= address
            ? entry : null;
    }

    public readonly record struct Entry(ulong Start, ulong End, ulong EarlierEnd, string? Plugin, string? Module, string? Method);
    private readonly record struct Range(ulong Start, ulong End, long ModuleId, string? Method, bool Ambiguous);
}
