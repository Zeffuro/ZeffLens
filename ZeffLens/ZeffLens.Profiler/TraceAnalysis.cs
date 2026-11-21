using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Etlx;
using Microsoft.Diagnostics.Tracing.EventPipe;
using ZeffLens.Diagnostics;

namespace ZeffLens.Profiler;

internal static class TraceAnalysis
{
    private const double MaxSampleGapMs = 20;

    public static ManagedProfile Read(ProfileRequest request, string tracePath, double seconds)
    {
        var selected = request.Targets ?? request.Assemblies.Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(name => new ProfileAssembly(request.Plugin, name)).ToArray();
        var methods = ManagedMethods.Read(tracePath, selected);
        var lost = 0;
        var etlx = TraceLog.CreateFromEventPipeDataFile(tracePath, options: new TraceLogOptions
        {
            OnLostEvents = (_, count, _) => lost = Math.Max(lost, count),
        });
        using var trace = new TraceLog(etlx);
        var source = trace.Events.GetSource();
        var rows = new Dictionary<HotspotKey, Totals>();
        var threads = new Dictionary<int, ThreadSample>();
        long samples = 0;
        long matched = 0;

        if (request.Mode == ManagedProfileMode.Stacks)
        {
            var sampler = new SampleProfilerTraceEventParser(source);
            sampler.ThreadSample += sample =>
            {
                var previous = threads.GetValueOrDefault(sample.ThreadID);
                var gap = sample.TimeStampRelativeMSec - previous.TimestampMs;
                // Count short gaps between managed samples.
                if (previous.Hotspot is { } hit && sample.Type == ClrThreadSampleType.Managed && gap is > 0 and <= MaxSampleGapMs)
                {
                    var totals = rows[hit];
                    rows[hit] = totals with { ManagedMs = totals.ManagedMs + gap };
                }

                var hotspot = Record(sample, 0, null, sample.Type == ClrThreadSampleType.Managed);
                threads[sample.ThreadID] = new ThreadSample(sample.TimeStampRelativeMSec,
                    sample.Type == ClrThreadSampleType.Managed ? hotspot : null);
            };
        }
        else
            source.Clr.GCAllocationTick += sample => Record(sample, sample.AllocationAmount64, sample.TypeName, false);

        source.Process();
        var hotspots = request.IsScan ? [] : rows.Select(row => new ManagedHotspot(row.Key.Module, row.Key.Method, row.Key.Leaf,
            row.Key.AllocationType, row.Value.Samples, row.Value.Bytes)
        {
            EstimatedManagedMs = row.Value.ManagedMs,
            EstimatedSelfMs = row.Key.IsLeaf ? row.Value.ManagedMs : 0,
        }).ToArray();
        var methodTotals = hotspots.GroupBy(row => (row.Module, row.Method)).Select(group =>
            new ManagedHotspot(group.Key.Module, group.Key.Method, null, null, group.Sum(row => row.Samples), group.Sum(row => row.SampledBytes))
            {
                EstimatedManagedMs = group.Sum(row => row.EstimatedManagedMs),
                EstimatedSelfMs = group.Sum(row => row.EstimatedSelfMs),
            });
        var pluginTotals = request.IsScan ? rows.GroupBy(row => row.Key.Plugin).Select(plugin =>
        {
            var topMethod = plugin.GroupBy(row => row.Key.Method)
                .OrderByDescending(method => method.Sum(row => row.Value.ManagedMs))
                .ThenByDescending(method => method.Sum(row => row.Value.ManagedSamples)).First().Key;
            return new PluginProfileTotal(plugin.Key, plugin.Sum(row => row.Value.Samples),
                plugin.Sum(row => row.Value.ManagedSamples), plugin.Sum(row => row.Value.ManagedMs), topMethod);
        }).OrderByDescending(row => row.EstimatedManagedMs).ThenByDescending(row => row.ManagedSamples).ToArray() : [];

        return new ManagedProfile(request.Plugin, request.Mode, seconds, samples, matched, lost, Top(hotspots))
        {
            RequestedAssemblies = selected.Length,
            ResolvedAssemblies = methods.ResolvedAssemblies,
            ResolvedMethods = methods.ResolvedMethods,
            AmbiguousAssemblies = methods.AmbiguousAssemblies,
            IsScan = request.IsScan,
            RequestedPlugins = selected.Select(item => item.Plugin).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
            ResolvedPlugins = methods.ResolvedPlugins,
            PluginTotals = pluginTotals,
            Methods = Top(methodTotals),
        };

        ManagedHotspot[] Top(IEnumerable<ManagedHotspot> values) => values
            .OrderByDescending(row => request.Mode == ManagedProfileMode.Stacks ? row.EstimatedManagedMs : row.SampledBytes)
            .ThenByDescending(row => row.Samples).Take(40).ToArray();

        HotspotKey? Record(TraceEvent sample, long bytes, string? allocationType, bool managed)
        {
            samples++;
            var stack = sample.CallStack();
            var leaf = request.IsScan ? null : stack?.CodeAddress.FullMethodName;
            for (var depth = 0; stack is not null && depth < 256; stack = stack.Caller, depth++)
            {
                if (methods.Find(stack.CodeAddress.Address) is not { Plugin: { } plugin, Module: { } module, Method: { } method })
                    continue;

                matched++;
                var key = new HotspotKey(plugin, module, method, depth > 0 ? leaf : null, allocationType, depth == 0);
                var counts = rows.GetValueOrDefault(key);
                rows[key] = new Totals(counts.Samples + 1, counts.ManagedSamples + (managed ? 1 : 0),
                    counts.Bytes + Math.Max(0, bytes), counts.ManagedMs);
                return key;
            }
            return null;
        }
    }

    private readonly record struct HotspotKey(string Plugin, string Module, string Method, string? Leaf, string? AllocationType, bool IsLeaf);
    private readonly record struct Totals(long Samples, long ManagedSamples, long Bytes, double ManagedMs);
    private readonly record struct ThreadSample(double TimestampMs, HotspotKey? Hotspot);
}
