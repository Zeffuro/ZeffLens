using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;

using ZeffLens.Diagnostics;

namespace ZeffLens;

internal static partial class ResourceProbeService
{
    private static NativeNodeProbeResult ProbeNativeNodeTrackers(
        ProbeTarget[] targets,
        Dictionary<string, List<string>> errorsByOwner)
    {
        var session = new NativeNodeProbeSession(targets, errorsByOwner);

        foreach (var (assembly, fallbackTarget) in GetKamiToolKitAssemblies(targets))
        {
            ProbeAllocatedKamiNodes(assembly, fallbackTarget, session);
            ProbeCreatedKamiNodes(assembly, fallbackTarget, session);
            ProbeCreatedKamiAddons(assembly, fallbackTarget, session);
        }

        return session.ToResult();
    }

    private static void ProbeAllocatedKamiNodes(Assembly assembly, ProbeTarget fallbackTarget, NativeNodeProbeSession session)
    {
        try
        {
            var libraryType = FindType(assembly, "KamiToolKit.KamiToolKitLibrary");
            var allocatedNodes = GetStaticEnumerable(libraryType, "AllocatedNodes");
            if (allocatedNodes is null || !session.SeenAllocatedNodeStores.Add(allocatedNodes))
            {
                return;
            }

            foreach (var entry in allocatedNodes)
            {
                var address = GetPropertyValue(entry, "Key");
                var nodeType = GetPropertyValue(entry, "Value") as Type;
                var owner = ResolveNativeNodeOwner(nodeType, session.Targets) ??
                            BuildUnresolvedNativeNodeOwner(nodeType, fallbackTarget);
                session.IncrementCount(owner, "AllocatedNodes");
                if (!session.TryReserveDetail(owner, "AllocatedNodes"))
                {
                    continue;
                }

                session.Details.Add(new OwnedNativeNodeProbe(
                    owner,
                    new NativeNodeProbeSnapshot(
                        "AllocatedNodes",
                        GetAssemblyDisplayName(assembly),
                        GetSafeTypeName(nodeType),
                        FormatAddress(address),
                        IsDisposed: null,
                        IsAddonRootNode: null,
                        IsOverlayAddon: null,
                        InternalName: null)));
            }
            session.RecordRegistry(fallbackTarget.Owner, KamiRegistries.Allocations);
        }
        catch (Exception ex)
        {
            AddProbeError(session.ErrorsByOwner, fallbackTarget.Owner.Key, $"Failed to read KamiToolKit AllocatedNodes: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static void ProbeCreatedKamiNodes(Assembly assembly, ProbeTarget fallbackTarget, NativeNodeProbeSession session)
    {
        try
        {
            var nodeBaseType = FindType(assembly, "KamiToolKit.BaseTypes.NodeBase", "KamiToolKit.NodeBase");
            var createdNodes = GetStaticEnumerable(nodeBaseType, "CreatedNodes");
            if (createdNodes is null)
            {
                return;
            }

            foreach (var node in createdNodes)
            {
                var owner = ResolveNativeNodeOwner(node.GetType(), session.Targets) ?? BuildUnresolvedNativeNodeOwner(node.GetType(), fallbackTarget);
                session.IncrementCount(owner, "CreatedNodes");
                if (!session.TryReserveDetail(owner, "CreatedNodes"))
                {
                    continue;
                }

                session.Details.Add(new OwnedNativeNodeProbe(
                    owner,
                    new NativeNodeProbeSnapshot(
                        "CreatedNodes",
                        GetAssemblyDisplayName(assembly),
                        GetSafeTypeName(node.GetType()),
                        Address: null,
                        TryReadBooleanField(node, "isDisposed"),
                        TryReadBooleanField(node, "IsAddonRootNode"),
                        IsOverlayAddon: null,
                        InternalName: null)));
            }
            session.RecordRegistry(fallbackTarget.Owner, KamiRegistries.Nodes);
        }
        catch (Exception ex)
        {
            AddProbeError(session.ErrorsByOwner, fallbackTarget.Owner.Key, $"Failed to read KamiToolKit CreatedNodes: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static void ProbeCreatedKamiAddons(Assembly assembly, ProbeTarget fallbackTarget, NativeNodeProbeSession session)
    {
        try
        {
            var nativeAddonType = FindType(assembly, "KamiToolKit.BaseTypes.NativeAddon", "KamiToolKit.NativeAddon");
            var createdAddons = GetStaticEnumerable(nativeAddonType, "CreatedAddons");
            if (createdAddons is null)
            {
                return;
            }

            foreach (var addon in createdAddons)
            {
                var owner = ResolveNativeNodeOwner(addon.GetType(), session.Targets) ?? BuildUnresolvedNativeNodeOwner(addon.GetType(), fallbackTarget);
                session.IncrementCount(owner, "CreatedAddons");
                if (!session.TryReserveDetail(owner, "CreatedAddons"))
                {
                    continue;
                }

                session.Details.Add(new OwnedNativeNodeProbe(
                    owner,
                    new NativeNodeProbeSnapshot(
                        "CreatedAddons",
                        GetAssemblyDisplayName(assembly),
                        GetSafeTypeName(addon.GetType()),
                        Address: null,
                        TryReadBooleanField(addon, "isDisposed"),
                        IsAddonRootNode: null,
                        IsOverlayAddon: null,
                        InternalName: null)));
            }
            session.RecordRegistry(fallbackTarget.Owner, KamiRegistries.Addons);
        }
        catch (Exception ex)
        {
            AddProbeError(session.ErrorsByOwner, fallbackTarget.Owner.Key, $"Failed to read KamiToolKit CreatedAddons: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static (Assembly Assembly, ProbeTarget FallbackTarget)[] GetKamiToolKitAssemblies(ProbeTarget[] targets)
    {
        var seen = new HashSet<Assembly>();
        var assemblies = new List<(Assembly Assembly, ProbeTarget FallbackTarget)>();
        foreach (var target in targets)
        {
            foreach (var assembly in SafeGetAssemblies(target.Context).Where(IsKamiToolKitAssembly))
            {
                if (seen.Add(assembly))
                {
                    assemblies.Add((assembly, target));
                }
            }
        }

        return assemblies.ToArray();
    }

    private static ProbeOwner? ResolveNativeNodeOwner(Type? nodeType, ProbeTarget[] targets)
    {
        var nodeAssembly = nodeType?.Assembly;
        var nodeContext = nodeAssembly is null ? null : AssemblyLoadContext.GetLoadContext(nodeAssembly);
        if (nodeContext is not null)
        {
            var target = targets.FirstOrDefault(candidate => ReferenceEquals(candidate.Context, nodeContext));
            if (target is not null)
            {
                return target.Owner;
            }
        }

        return null;
    }

    private static ProbeOwner BuildUnresolvedNativeNodeOwner(Type? nodeType, ProbeTarget fallbackTarget)
    {
        var assembly = nodeType?.Assembly;
        var context = assembly is null ? null : AssemblyLoadContext.GetLoadContext(assembly);
        if (assembly is null || context is null)
        {
            return ProbeOwner.Global;
        }

        var internalName = assembly.GetName().Name ?? fallbackTarget.Owner.InternalName;
        var contextId = RuntimeHelpers.GetHashCode(context);
        return new ProbeOwner(
            ProbeOwner.BuildKey(internalName, contextId, generation: 0),
            internalName,
            internalName,
            Generation: 0,
            IsLoaded: false,
            IsRetiredContext: false,
            context.Name,
            contextId);
    }

    private sealed class NativeNodeProbeSession(
        ProbeTarget[] targets,
        Dictionary<string, List<string>> errorsByOwner)
    {
        private readonly Dictionary<(string OwnerKey, string TrackerName), (ProbeOwner Owner, int Count)> counts = new();
        private readonly Dictionary<string, KamiRegistries> readRegistries = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, int> detailCountsByOwnerAndTracker = new(StringComparer.OrdinalIgnoreCase);
        private readonly int maxDetailsPerPlugin = Math.Clamp(PluginState.Config.Diagnostics.MaxResourceProbeDetailsPerPlugin, 20, 1000);

        public ProbeTarget[] Targets { get; } = targets;

        public Dictionary<string, List<string>> ErrorsByOwner { get; } = errorsByOwner;

        public List<OwnedNativeNodeProbe> Details { get; } = [];

        public HashSet<object> SeenAllocatedNodeStores { get; } = new(ReferenceEqualityComparer.Instance);

        public void RecordRegistry(ProbeOwner owner, KamiRegistries registry)
            => readRegistries[owner.Key] = readRegistries.GetValueOrDefault(owner.Key) | registry;

        public void IncrementCount(ProbeOwner owner, string trackerName)
        {
            var key = (owner.Key, trackerName);
            counts[key] = counts.TryGetValue(key, out var count)
                              ? (count.Owner, count.Count + 1)
                              : (owner, 1);
        }

        public bool TryReserveDetail(ProbeOwner owner, string trackerName)
        {
            var key = $"{owner.Key}\u001F{trackerName}";
            detailCountsByOwnerAndTracker.TryGetValue(key, out var count);
            if (count >= maxDetailsPerPlugin)
            {
                return false;
            }

            detailCountsByOwnerAndTracker[key] = count + 1;
            return true;
        }

        public NativeNodeProbeResult ToResult()
        {
            return new NativeNodeProbeResult(
                Details.ToArray(),
                counts.Select(count => new OwnedNativeNodeCount(count.Value.Owner, count.Key.TrackerName, count.Value.Count)).ToArray(),
                readRegistries);
        }
    }

    private static Type? FindType(Assembly assembly, params string[] typeNames)
        => typeNames.Select(assembly.GetType).FirstOrDefault(type => type is not null);

    private static IEnumerable? GetStaticEnumerable(Type? type, string memberName)
    {
        const BindingFlags flags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

        try
        {
            var property = FindProperty(type, memberName, flags);
            if (property is not null && property.GetIndexParameters().Length == 0)
            {
                return property.GetValue(null) as IEnumerable;
            }

            return FindField(type, memberName, flags)?.GetValue(null) as IEnumerable;
        }
        catch
        {
            return null;
        }
    }


}
