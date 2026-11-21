using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;

using Dalamud.Plugin;
using ZeffLens.Diagnostics;

namespace ZeffLens;

internal static partial class ResourceProbeService
{
    private static readonly string[] NativeUiAssemblyNamePrefixes =
    {
        "KamiToolKit",
    };

    public static PluginResourceProbeSnapshot[] Refresh(ProbeTarget[] targets, DateTime timestampUtc, string reason, bool focused = false)
    {
        var owners = targets.Select(target => target.Owner).ToDictionary(owner => owner.Key, StringComparer.OrdinalIgnoreCase);
        var errorsByOwner = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        var hookProbes = ProbeDalamudHooks(targets, errorsByOwner, focused, out var readHooks);
        foreach (var probe in hookProbes)
        {
            owners.TryAdd(probe.Owner.Key, probe.Owner);
        }

        var nativeNodeProbeResult = ProbeNativeNodeTrackers(targets, errorsByOwner);
        foreach (var probe in nativeNodeProbeResult.Details)
        {
            owners.TryAdd(probe.Owner.Key, probe.Owner);
        }
        foreach (var count in nativeNodeProbeResult.Counts)
        {
            owners.TryAdd(count.Owner.Key, count.Owner);
        }

        var staticFieldProbes = Array.Empty<OwnedStaticFieldProbe>();
        if (PluginState.Config.Diagnostics.ScanStaticFieldsForResourceProbes)
        {
            var staticFieldTargets = targets.Where(target => target.Owner.IsRetiredContext).ToArray();
            if (staticFieldTargets.Length > 0)
            {
                try
                {
                    staticFieldProbes = ProbeStaticFields(staticFieldTargets, errorsByOwner);
                }
                catch (Exception ex)
                {
                    AddProbeError(errorsByOwner, ProbeOwner.Global.Key, $"Static field probe failed: {ex.GetType().Name}: {ex.Message}");
                    Service.Logger.Warning(ex, "Failed to probe static fields.");
                }
            }
        }

        foreach (var ownerKey in errorsByOwner.Keys)
        {
            owners.TryAdd(
                ownerKey,
                ownerKey.Equals(ProbeOwner.Global.Key, StringComparison.OrdinalIgnoreCase)
                    ? ProbeOwner.Global
                    : new ProbeOwner(ownerKey, ownerKey, ownerKey, 0, IsLoaded: false, IsRetiredContext: false, LoadContextName: null, ContextId: 0));
        }

        var hooksByOwner = hookProbes.ToLookup(probe => probe.Owner.Key, probe => probe.Snapshot, StringComparer.OrdinalIgnoreCase);
        var fieldsByOwner = staticFieldProbes.ToLookup(probe => probe.Owner.Key, probe => probe.Snapshot, StringComparer.OrdinalIgnoreCase);
        var nodesByOwner = nativeNodeProbeResult.Details.ToLookup(probe => probe.Owner.Key, probe => probe.Snapshot, StringComparer.OrdinalIgnoreCase);
        var countsByOwner = nativeNodeProbeResult.Counts.ToLookup(count => count.Owner.Key, StringComparer.OrdinalIgnoreCase);
        var probeSnapshots = owners.Values
                                   .Select(BuildSnapshot)
                                   .Where(probe => (focused && probe.Generation > 0) ||
                                       probe.HookCount > 0 || probe.StaticFields.Length > 0 ||
                                       probe.KtkAllocatedNodeCount > 0 || probe.KtkCreatedNodeCount > 0 ||
                                       probe.KtkCreatedAddonCount > 0 || probe.NativeNodes.Length > 0 || probe.Errors.Length > 0)
                                   .OrderByDescending(probe => probe.IsRetiredContext && probe.UndisposedHookCount > 0)
                                   .ThenByDescending(probe => probe.IsRetiredContext && probe.NativeNodeTrackerCount > 0)
                                   .ThenBy(probe => probe.InternalName, StringComparer.OrdinalIgnoreCase)
                                   .ThenByDescending(probe => probe.Generation)
                                   .ToArray();

        PluginResourceProbeSnapshot BuildSnapshot(ProbeOwner owner)
        {
            var hooks = hooksByOwner[owner.Key].ToArray();
            var fields = fieldsByOwner[owner.Key].ToArray();
            var nodes = nodesByOwner[owner.Key].ToArray();
            var counts = countsByOwner[owner.Key];
            var errors = errorsByOwner.TryGetValue(owner.Key, out var ownerErrors)
                ? ownerErrors.Distinct(StringComparer.OrdinalIgnoreCase).ToArray()
                : Array.Empty<string>();
            var allocations = counts.Where(count => count.TrackerName == "AllocatedNodes").Sum(count => count.Count);
            var registeredNodes = counts.Where(count => count.TrackerName == "CreatedNodes").Sum(count => count.Count);
            var addons = counts.Where(count => count.TrackerName == "CreatedAddons").Sum(count => count.Count);

            return new PluginResourceProbeSnapshot(
                owner.InternalName, owner.Name, owner.Generation, owner.IsLoaded, owner.IsRetiredContext,
                owner.LoadContextName, owner.ContextId, timestampUtc,
                hooks.Length, hooks.Count(hook => hook.IsEnabled == true), hooks.Count(hook => hook.IsDisposed == false), hooks,
                fields.Count(field => field.IsDisposable), fields.Count(field => field.IsDisposable && field.IsDisposed == false),
                fields.Count(field => field.IsNativeUiCandidate), allocations, registeredNodes, addons, allocations, addons,
                nodes, fields, errors)
            {
                ReadHookRegistry = readHooks,
                ReadKamiRegistries = nativeNodeProbeResult.ReadRegistries.GetValueOrDefault(owner.Key),
            };
        }

        Service.Logger.Verbose(
            "Resource probe refreshed ({Reason}). owners={OwnerCount}, hooks={HookCount}, staticFields={StaticFieldCount}, ktkAllocationEntries={AllocationCount}",
            reason,
            probeSnapshots.Length,
            probeSnapshots.Sum(probe => probe.HookCount),
            probeSnapshots.Sum(probe => probe.StaticFields.Length),
            probeSnapshots.Sum(probe => probe.KtkAllocatedNodeCount));

        return probeSnapshots;
    }

    private static OwnedHookProbe[] ProbeDalamudHooks(
        ProbeTarget[] targets, Dictionary<string, List<string>> errorsByOwner, bool focused, out bool complete)
    {
        var hooks = new List<OwnedHookProbe>();
        complete = false;

        try
        {
            var dalamudAssembly = typeof(IDalamudPluginInterface).Assembly;
            var hookManagerType = dalamudAssembly.GetType("Dalamud.Hooking.Internal.HookManager");
            var trackedHooksProperty = hookManagerType?.GetProperty(
                "TrackedHooks",
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            var trackedHooks = trackedHooksProperty?.GetValue(null) as IEnumerable;

            if (trackedHooks is null)
            {
                AddProbeError(errorsByOwner, ProbeOwner.Global.Key, "Dalamud HookManager.TrackedHooks was not available.");
                return hooks.ToArray();
            }

            foreach (var entry in trackedHooks)
            {
                var hookInfo = entry.GetType().GetProperty("Value")?.GetValue(entry);
                if (hookInfo is null)
                {
                    continue;
                }

                var assembly = GetPropertyValue(hookInfo, "Assembly") as Assembly;
                var owner = ResolveHookOwner(assembly, targets, focused);
                if (owner is null)
                    continue;

                var hook = GetPropertyValue(hookInfo, "Hook");
                var hookDelegate = GetPropertyValue(hookInfo, "Delegate") as Delegate;

                hooks.Add(new OwnedHookProbe(
                    owner,
                    new HookProbeSnapshot(
                        owner.InternalName,
                        owner.Name,
                        owner.Generation,
                        owner.IsRetiredContext,
                        assembly is null ? "<unknown assembly>" : GetAssemblyDisplayName(assembly),
                        hook?.GetType().FullName ?? "<unknown hook>",
                        hookDelegate?.GetType().FullName ?? "<unknown delegate>",
                        TryReadStringProperty(hook, "BackendName") ?? "<unknown>",
                        FormatPointer(TryReadIntPtrProperty(hook, "Address")),
                        TryReadBooleanProperty(hook, "IsEnabled"),
                        TryReadBooleanProperty(hook, "IsDisposed"))
                    {
                        RegistryId = GetPropertyValue(entry, "Key") is Guid id ? id : null,
                    }));
            }
            complete = true;
        }
        catch (Exception ex)
        {
            AddProbeError(errorsByOwner, ProbeOwner.Global.Key, $"Hook probe failed: {ex.GetType().Name}: {ex.Message}");
            Service.Logger.Warning(ex, "Failed to probe Dalamud hooks.");
        }

        return hooks.ToArray();
    }

    private static OwnedStaticFieldProbe[] ProbeStaticFields(
        ProbeTarget[] targets,
        Dictionary<string, List<string>> errorsByOwner)
    {
        var probes = new List<OwnedStaticFieldProbe>();
        var maxDetailsPerPlugin = Math.Clamp(PluginState.Config.Diagnostics.MaxResourceProbeDetailsPerPlugin, 20, 1000);

        foreach (var target in targets)
        {
            var detailCount = 0;
            foreach (var assembly in SafeGetAssemblies(target.Context).Where(assembly => ShouldScanStaticFields(assembly, target)))
            {
                foreach (var type in SafeGetTypes(assembly, target.Owner, errorsByOwner))
                {
                    if (type.ContainsGenericParameters || IsCompilerGeneratedCache(type))
                    {
                        continue;
                    }

                    FieldInfo[] fields;
                    try
                    {
                        fields = type.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                    }
                    catch (Exception ex)
                    {
                        AddProbeError(errorsByOwner, target.Owner.Key, $"Failed to inspect fields on {type.FullName}: {ex.GetType().Name}: {ex.Message}");
                        continue;
                    }

                    foreach (var field in fields)
                    {
                        if (field.IsLiteral || field.IsSpecialName)
                        {
                            continue;
                        }

                        Type fieldType;
                        try
                        {
                            fieldType = field.FieldType;
                        }
                        catch (Exception ex)
                        {
                            AddProbeError(errorsByOwner, target.Owner.Key, $"Failed to inspect field type for {type.FullName}.{field.Name}: {ex.GetType().Name}: {ex.Message}");
                            continue;
                        }

                        if (fieldType.ContainsGenericParameters)
                        {
                            continue;
                        }

                        var isDisposable = typeof(IDisposable).IsAssignableFrom(fieldType) ||
                                           typeof(IAsyncDisposable).IsAssignableFrom(fieldType);
                        var isNativeUiCandidate = IsNativeUiCandidate(field, fieldType);
                        if (!isDisposable && !isNativeUiCandidate)
                        {
                            continue;
                        }

                        if (detailCount >= maxDetailsPerPlugin)
                        {
                            continue;
                        }

                        detailCount++;
                        probes.Add(new OwnedStaticFieldProbe(
                            target.Owner,
                            new StaticFieldProbeSnapshot(
                                GetAssemblyDisplayName(assembly),
                                type.FullName ?? type.Name,
                                field.Name,
                                fieldType.FullName ?? fieldType.Name,
                                ValueType: null,
                                isDisposable,
                                IsDisposed: null,
                                isNativeUiCandidate,
                                CollectionCount: null,
                                DisplayValue: null)));
                    }
                }
            }
        }

        return probes.ToArray();
    }

    private static ProbeOwner? ResolveHookOwner(Assembly? assembly, ProbeTarget[] targets, bool focused)
    {
        var context = assembly is null ? null : AssemblyLoadContext.GetLoadContext(assembly);
        var target = context is null
                         ? null
                         : targets.FirstOrDefault(candidate => ReferenceEquals(candidate.Context, context));

        if (target is not null)
        {
            return target.Owner;
        }

        if (focused)
            return null;

        IExposedPlugin? plugin = null;
        if (assembly is not null)
        {
            try
            {
                plugin = Service.PluginInterface.GetPlugin(assembly);
            }
            catch (Exception ex)
            {
                var assemblyName = assembly.FullName ?? assembly.GetName().Name ?? "<unknown>";
                Service.Logger.Verbose(ex, "Failed to resolve hook owner for assembly {Assembly}.", assemblyName);
            }
        }

        var internalName = plugin?.InternalName ?? assembly?.GetName().Name ?? "<unresolved>";
        var contextId = context is null ? 0 : RuntimeHelpers.GetHashCode(context);
        var isLoaded = plugin?.IsLoaded == true;

        return new ProbeOwner(
            ProbeOwner.BuildKey(internalName, contextId, generation: 0),
            internalName,
            plugin?.Name ?? internalName,
            Generation: 0,
            isLoaded,
            IsRetiredContext: false,
            context?.Name,
            contextId);
    }

    private static bool ShouldScanStaticFields(Assembly assembly, ProbeTarget target)
    {
        var assemblyName = assembly.GetName().Name ?? string.Empty;
        return assemblyName.Equals(target.Owner.InternalName, StringComparison.OrdinalIgnoreCase) ||
               assemblyName.Equals(target.Owner.Name, StringComparison.OrdinalIgnoreCase) ||
               assemblyName.StartsWith(target.Owner.InternalName + ".", StringComparison.OrdinalIgnoreCase) ||
               NativeUiAssemblyNamePrefixes.Any(prefix => assemblyName.Equals(prefix, StringComparison.OrdinalIgnoreCase) ||
                                                          assemblyName.StartsWith(prefix + ".", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsKamiToolKitAssembly(Assembly assembly)
    {
        var assemblyName = assembly.GetName().Name ?? string.Empty;
        return NativeUiAssemblyNamePrefixes.Any(prefix => assemblyName.Equals(prefix, StringComparison.OrdinalIgnoreCase) ||
                                                          assemblyName.StartsWith(prefix + ".", StringComparison.OrdinalIgnoreCase));
    }

    private static Assembly[] SafeGetAssemblies(AssemblyLoadContext context)
    {
        try
        {
            return context.Assemblies.ToArray();
        }
        catch
        {
            return Array.Empty<Assembly>();
        }
    }

    private static IEnumerable<Type> SafeGetTypes(
        Assembly assembly,
        ProbeOwner owner,
        Dictionary<string, List<string>> errorsByOwner)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            AddProbeError(errorsByOwner, owner.Key, $"Partial type load for {GetAssemblyDisplayName(assembly)}: {ex.Message}");
            return ex.Types.WhereNotNull();
        }
        catch (Exception ex)
        {
            AddProbeError(errorsByOwner, owner.Key, $"Failed to load types from {GetAssemblyDisplayName(assembly)}: {ex.GetType().Name}: {ex.Message}");
            return Array.Empty<Type>();
        }
    }

    private static object? GetPropertyValue(object value, string propertyName)
        => FindProperty(value.GetType(), propertyName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
           ?.GetValue(value);

    private static bool? TryReadBooleanProperty(object? value, params string[] propertyNames)
    {
        if (value is null)
        {
            return null;
        }

        foreach (var propertyName in propertyNames)
        {
            try
            {
                var property = FindProperty(value.GetType(), propertyName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (property?.PropertyType == typeof(bool) && property.GetIndexParameters().Length == 0)
                {
                    return (bool?)property.GetValue(value);
                }
            }
            catch
            {
            }
        }

        return null;
    }

    private static bool? TryReadBooleanField(object? value, string fieldName)
    {
        if (value is null)
        {
            return null;
        }

        try
        {
            var field = FindField(value.GetType(), fieldName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            return field?.FieldType == typeof(bool)
                       ? (bool?)field.GetValue(value)
                       : null;
        }
        catch
        {
            return null;
        }
    }

    private static string? TryReadStringProperty(object? value, string propertyName)
    {
        if (value is null)
        {
            return null;
        }

        try
        {
            var property = FindProperty(value.GetType(), propertyName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            return property?.PropertyType == typeof(string) && property.GetIndexParameters().Length == 0
                       ? property.GetValue(value) as string
                       : null;
        }
        catch
        {
            return null;
        }
    }

    private static IntPtr TryReadIntPtrProperty(object? value, string propertyName)
    {
        if (value is null)
        {
            return IntPtr.Zero;
        }

        try
        {
            var property = FindProperty(value.GetType(), propertyName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (property?.PropertyType == typeof(IntPtr) && property.GetIndexParameters().Length == 0)
            {
                return (IntPtr)(property.GetValue(value) ?? IntPtr.Zero);
            }
        }
        catch
        {
        }

        return IntPtr.Zero;
    }

    private static bool IsNativeUiCandidate(FieldInfo field, Type fieldType)
    {
        var haystack = field.Name + "|" + fieldType.FullName;
        return haystack.Contains("NativeAddon", StringComparison.OrdinalIgnoreCase) ||
               haystack.Contains("KamiToolKit", StringComparison.OrdinalIgnoreCase) ||
               haystack.Contains("Atk", StringComparison.OrdinalIgnoreCase) ||
               haystack.Contains("Addon", StringComparison.OrdinalIgnoreCase) ||
               haystack.Contains("Overlay", StringComparison.OrdinalIgnoreCase) ||
               haystack.Contains("Node", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsCompilerGeneratedCache(Type valueType)
        => valueType.IsDefined(typeof(CompilerGeneratedAttribute), inherit: false) ||
           valueType.Name.Contains("<>", StringComparison.Ordinal);

    private static PropertyInfo? FindProperty(Type? type, string propertyName, BindingFlags bindingFlags)
    {
        for (var current = type; current is not null; current = current.BaseType)
        {
            var property = current.GetProperty(propertyName, bindingFlags | BindingFlags.DeclaredOnly);
            if (property is not null)
            {
                return property;
            }
        }

        return null;
    }

    private static FieldInfo? FindField(Type? type, string fieldName, BindingFlags bindingFlags)
    {
        for (var current = type; current is not null; current = current.BaseType)
        {
            var field = current.GetField(fieldName, bindingFlags | BindingFlags.DeclaredOnly);
            if (field is not null)
            {
                return field;
            }
        }

        return null;
    }

    private static void AddProbeError(Dictionary<string, List<string>> errorsByOwner, string ownerKey, string error)
    {
        if (!errorsByOwner.TryGetValue(ownerKey, out var errors))
        {
            errors = new List<string>();
            errorsByOwner[ownerKey] = errors;
        }

        if (errors.Count < 50)
        {
            errors.Add(error);
        }
    }

    private static string GetAssemblyDisplayName(Assembly assembly)
    {
        var assemblyName = assembly.GetName();
        return assemblyName.Version is null
                   ? assemblyName.Name ?? assembly.FullName ?? "<unknown>"
                   : $"{assemblyName.Name} {assemblyName.Version}";
    }

    private static string GetSafeTypeName(Type? type)
    {
        if (type is null)
        {
            return "<unknown>";
        }

        if (!type.IsGenericType)
        {
            return type.FullName ?? type.Name;
        }

        var genericDefinitionName = type.GetGenericTypeDefinition().FullName ?? type.Name;
        var tickIndex = genericDefinitionName.IndexOf('`', StringComparison.Ordinal);
        if (tickIndex >= 0)
        {
            genericDefinitionName = genericDefinitionName[..tickIndex];
        }

        return $"{genericDefinitionName}<{string.Join(", ", type.GetGenericArguments().Select(GetSafeTypeName))}>";
    }

    private static string FormatPointer(IntPtr pointer)
        => pointer == IntPtr.Zero ? "0x0" : $"0x{pointer.ToInt64():X}";

    private static string? FormatAddress(object? address)
    {
        try
        {
            return address switch
            {
                null => null,
                IntPtr pointer => FormatPointer(pointer),
                UIntPtr pointer => $"0x{pointer.ToUInt64():X}",
                _ => $"0x{Convert.ToInt64(address):X}",
            };
        }
        catch
        {
            return address?.ToString();
        }
    }

    private sealed record OwnedHookProbe(ProbeOwner Owner, HookProbeSnapshot Snapshot);

    private sealed record OwnedStaticFieldProbe(ProbeOwner Owner, StaticFieldProbeSnapshot Snapshot);

    private sealed record OwnedNativeNodeProbe(ProbeOwner Owner, NativeNodeProbeSnapshot Snapshot);

    private sealed record OwnedNativeNodeCount(ProbeOwner Owner, string TrackerName, int Count);

    private sealed record NativeNodeProbeResult(
        OwnedNativeNodeProbe[] Details, OwnedNativeNodeCount[] Counts, Dictionary<string, KamiRegistries> ReadRegistries);
}
