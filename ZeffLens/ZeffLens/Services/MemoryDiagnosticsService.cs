using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;

using Dalamud.Plugin;
using ZeffLens.Diagnostics;

namespace ZeffLens;

public sealed partial class MemoryDiagnosticsService : IDisposable
{
    private readonly object _gate = new();
    private readonly Process _process = Process.GetCurrentProcess();
    private readonly List<MemorySnapshot> _snapshots = new();
    private readonly List<PluginMemoryEvent> _events = new();
    private readonly Dictionary<string, TrackedPluginContext> _activePluginContexts = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<RetiredPluginContext> _retiredPluginContexts = new();
    private readonly Dictionary<string, int> _nextContextGenerationByInternalName = new(StringComparer.OrdinalIgnoreCase);

    private PluginMemoryEvent[]? _eventView;
    private PluginContextSnapshot[]? _contextView;
    private string? _requestedProbe;

    private PluginResourceProbeSnapshot[] _resourceProbes = Array.Empty<PluginResourceProbeSnapshot>();
    private int _lastContextCount;
    private int _nextSnapshotId;
    private int _nextEventId;
    private volatile bool _disposed;

    internal FrameRecorder Frames { get; } = new(GameFrameProbe.Read, GraphicsMemoryProbe.Capture);
    internal AddonWatch Addons { get; }
    internal AddonProfiler AddonProfiles { get; }
    internal ManagedProfiler ManagedProfiles { get; }

    public MemoryDiagnosticsService()
    {
        Addons = new AddonWatch(Service.AddonLifecycle, Frames);
        var logger = Service.Logger;
        AddonProfiles = new AddonProfiler(Service.AddonLifecycle, error => logger.Warning(error, "Addon profiler failed."),
            name => VisibleAddons.CaptureProfile(Service.Framework, name));
        ManagedProfiles = new ManagedProfiler(
            Path.Combine(Service.PluginInterface.AssemblyLocation.DirectoryName!, "profiler", "ZeffLens.Profiler.exe"),
            GetProfilingAssemblies, () => System.Threading.Volatile.Read(ref _pluginChangeRevision),
            error => logger.Warning(error, "Managed profiler failed."));
        Service.PluginInterface.ActivePluginsChanged += OnActivePluginsChanged;
        Baseline = CaptureSnapshot("Initial baseline", includeResourceProbe: false);
        _lastAutoSample = Stopwatch.GetTimestamp();
        Service.Framework.Update += OnFrameworkUpdate;
    }

    public MemorySnapshot? Baseline { get; private set; }

    public string? LastExportPath { get; private set; }

    internal MemorySnapshot[] GetRecentSamples(int count)
    {
        lock (_gate)
            return _snapshots.Skip(Math.Max(0, _snapshots.Count - count)).ToArray();
    }

    public PluginMemoryEvent[] GetEvents()
    {
        lock (_gate)
        {
            return _eventView ??= _events.ToArray();
        }
    }

    public PluginContextSnapshot[] GetPluginContexts()
    {
        lock (_gate)
        {
            return _contextView ??= _activePluginContexts.Values
                       .Select(context => context.ToSnapshot())
                       .Concat(_retiredPluginContexts.Select(context => context.ToSnapshot()))
                       .OrderByDescending(context => context.SuspectedRetainedAfterUnload)
                       .ThenBy(context => context.InternalName, StringComparer.OrdinalIgnoreCase)
                       .ThenByDescending(context => context.Generation)
                       .ToArray();
        }
    }

    internal (PluginResourceProbeSnapshot[] Probes, DateTime? Timestamp) GetResourceView()
    {
        lock (_gate)
        {
            return (_resourceProbes, _lastProbeUtc);
        }
    }

    public MemorySnapshot SetBaseline(string reason = "Manual baseline")
    {
        var snapshot = CaptureSnapshot(reason);
        lock (_gate)
        {
            Baseline = snapshot;
        }

        return snapshot;
    }

    public MemorySnapshot CaptureSnapshot(string reason, bool includeResourceProbe = true)
    {
        Frames.NoteInterference();
        var probe = includeResourceProbe && PluginState.Config.Diagnostics.CaptureHookProbesOnSnapshots;
        MemorySnapshot snapshot;
        lock (_gate)
        {
            ThrowIfDisposed();
            snapshot = Sample(reason, ObservePluginContexts(includeResourceProbe));
        }
        if (probe)
            RequestResourceProbe(reason);

        return snapshot;
    }

    private MemorySnapshot Sample(string reason, PluginContextObservations? observations = null, bool forcedGcJustRan = false)
    {
        var timestamp = DateTime.UtcNow;
        lock (_gate)
        {
            ThrowIfDisposed();
            if (observations is not null)
                ApplyObservations(timestamp, observations);

            var snapshot = CaptureProcessSnapshot(reason, timestamp, _lastContextCount, _activePluginContexts.Count);
            UpdateRetentionMarkers(forcedGcJustRan);
            _contextView = null;
            _snapshots.Add(snapshot);
            TrimSnapshots();
            TrimRetiredContexts();
            return snapshot;
        }
    }

    private void ApplyObservations(DateTime timestamp, PluginContextObservations observations)
    {
        _lastContextCount = observations.AssemblyLoadContextCount;
        ApplyPluginContextObservations(timestamp, observations.PluginContexts);
        _contextView = null;
    }

    internal (MemorySnapshot? Latest, MemorySnapshot? Previous, MemorySnapshot? Baseline, int Count) GetMemoryOverview()
    {
        lock (_gate)
        {
            var count = _snapshots.Count;
            return (count > 0 ? _snapshots[^1] : null, count > 1 ? _snapshots[^2] : null, Baseline, count);
        }
    }

    internal bool ProbePending => System.Threading.Volatile.Read(ref _requestedProbe) is not null;
    private DateTime? _lastProbeUtc;

    internal void RequestResourceProbe(string reason = "Manual resource probe")
        => System.Threading.Interlocked.Exchange(ref _requestedProbe, reason);

    private void RefreshResourceProbes(string reason)
    {
        ThrowIfDisposed();

        Frames.NoteInterference();
        var timestamp = DateTime.UtcNow;
        var targets = GetProbeTargets();
        var probeSnapshots = ResourceProbeService.Refresh(targets, timestamp, reason);

        lock (_gate)
        {
            _resourceProbes = probeSnapshots;
            _lastProbeUtc = timestamp;
        }
    }

    public void ClearHistory()
    {
        lock (_memoryActionGate)
        lock (_gate)
        {
            _snapshots.Clear();
            _events.Clear();
            _eventView = null;
            _contextView = null;
            _activePluginContexts.Clear();
            _retiredPluginContexts.Clear();
            _nextContextGenerationByInternalName.Clear();
            _resourceProbes = Array.Empty<PluginResourceProbeSnapshot>();
            _reloadRuns.Clear();
            _reloadRunView = null;
            _settleAt = 0;
            _pendingChanges.Clear();
            _requestedProbe = null;
            _lastProbeUtc = null;
            _nextSnapshotId = 0;
            _nextEventId = 0;
            _lastReleaseResult = null;
            System.Threading.Volatile.Write(ref _memoryActionFollowUpAt, 0);
            LastExportPath = null;
            ResetReloadComparison();
            Baseline = CaptureSnapshot("History cleared", includeResourceProbe: false);
        }

        Frames.Clear();
        Addons.Clear();
        AddonProfiles.Clear();
        ManagedProfiles.Clear();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        lock (_gate)
        {
            _disposed = true;
            _process.Dispose();
        }
        Frames.Clear();
        ManagedProfiles.Cancel();

        Service.PluginInterface.ActivePluginsChanged -= OnActivePluginsChanged;
        Service.Framework.Update -= OnFrameworkUpdate;
        try
        {
            Addons.Dispose();
        }
        finally
        {
            AddonProfiles.Dispose();
        }
    }

    private PluginContextObservations ObservePluginContexts(bool includeAssemblyNames)
    {
        try
        {
            var contexts = AssemblyLoadContext.All.ToArray();
            var pluginContexts = new List<PluginContextObservation>();

            foreach (var context in contexts)
            {
                IExposedPlugin? plugin;
                try
                {
                    plugin = Service.PluginInterface.GetPlugin(context);
                }
                catch (Exception ex)
                {
                    Service.Logger.Verbose(ex, "Failed to resolve plugin for AssemblyLoadContext {ContextName}", context.Name ?? "<unnamed>");
                    continue;
                }

                if (plugin is null)
                {
                    continue;
                }

                var assemblyNames = Array.Empty<string>();
                var assemblyCount = 0;
                if (includeAssemblyNames)
                {
                    var assemblies = SafeGetAssemblies(context);
                    assemblyCount = assemblies.Length;
                    assemblyNames = assemblies
                                    .Select(GetAssemblyDisplayName)
                                    .Where(name => !string.IsNullOrWhiteSpace(name))
                                    .Distinct(StringComparer.OrdinalIgnoreCase)
                                    .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                                    .ToArray();
                }

                pluginContexts.Add(new PluginContextObservation(
                    plugin.InternalName,
                    plugin.Name,
                    plugin.IsLoaded,
                    plugin.IsDev,
                    plugin.IsTesting,
                    plugin.IsThirdParty,
                    context.Name,
                    GetContextId(context),
                    assemblyCount,
                    assemblyNames,
                    context));
            }

            return new PluginContextObservations(contexts.Length, pluginContexts.ToArray());
        }
        catch (Exception ex)
        {
            Service.Logger.Warning(ex, "Failed to observe plugin AssemblyLoadContexts.");
            return new PluginContextObservations(AssemblyLoadContextCount: 0, Array.Empty<PluginContextObservation>());
        }
    }

    private void ApplyPluginContextObservations(DateTime timestamp, IEnumerable<PluginContextObservation> observations)
    {
        foreach (var observation in observations)
        {
            if (string.IsNullOrWhiteSpace(observation.InternalName))
            {
                continue;
            }

            if (!observation.IsLoaded)
            {
                continue;
            }

            if (_activePluginContexts.TryGetValue(observation.InternalName, out var trackedContext) &&
                !trackedContext.IsSameContext(observation.Context))
            {
                RetireActiveContext(
                    observation.InternalName,
                    timestamp,
                    $"Observed replacement AssemblyLoadContext #{observation.ContextId}");
                trackedContext = null;
            }

            if (trackedContext is null)
            {
                trackedContext = new TrackedPluginContext(observation.InternalName, NextGeneration(observation.InternalName));
                _activePluginContexts[observation.InternalName] = trackedContext;
            }

            trackedContext.UpdateFrom(observation, timestamp);
        }
    }

    private void RetireActiveContext(string internalName, DateTime timestamp, string reason)
    {
        if (!_activePluginContexts.TryGetValue(internalName, out var context))
        {
            return;
        }

        context.IsLoaded = false;
        context.LastUnloadUtc = timestamp;
        AddRetiredContext(RetiredPluginContext.FromTracked(context, timestamp, reason));
        _activePluginContexts.Remove(internalName);
    }

    private void AddRetiredContext(RetiredPluginContext retiredContext)
    {
        if (_retiredPluginContexts.Any(context =>
                context.ContextReference.TryGetTarget(out var existing) &&
                retiredContext.ContextReference.TryGetTarget(out var retired) && ReferenceEquals(existing, retired)))
        {
            return;
        }

        retiredContext.UpdateRetentionMarkers(forcedGcJustRan: false, DateTime.UtcNow);
        _retiredPluginContexts.Add(retiredContext);
    }

    private void UpdateRetentionMarkers(bool forcedGcJustRan)
    {
        var timestamp = DateTime.UtcNow;
        foreach (var trackedContext in _activePluginContexts.Values)
        {
            trackedContext.SuspectedRetainedAfterUnload = false;
        }

        foreach (var retiredContext in _retiredPluginContexts)
        {
            retiredContext.UpdateRetentionMarkers(forcedGcJustRan, timestamp);
        }
    }

    private ProbeTarget[] GetProbeTargets()
    {
        lock (_gate)
        {
            var activeTargets = _activePluginContexts.Values
                                                     .Select(context => context.ToProbeTarget())
                                                     .WhereNotNull();
            var retiredTargets = _retiredPluginContexts
                                 .Select(context => context.ToProbeTarget())
                                 .WhereNotNull();

            return activeTargets
                   .Concat(retiredTargets)
                   .OrderBy(target => target.Owner.InternalName, StringComparer.OrdinalIgnoreCase)
                   .ThenByDescending(target => target.Owner.Generation)
                   .ToArray();
        }
    }

    private void TrimSnapshots()
    {
        var maxSamples = Math.Clamp(PluginState.Config.Diagnostics.MaxSamples, 20, 2000);
        if (_snapshots.Count <= maxSamples)
        {
            return;
        }

        _snapshots.RemoveRange(0, _snapshots.Count - maxSamples);
    }

    private void TrimEvents()
    {
        var maxEvents = Math.Clamp(PluginState.Config.Diagnostics.MaxEvents, 20, 1000);
        if (_events.Count <= maxEvents)
        {
            return;
        }

        _events.RemoveRange(0, _events.Count - maxEvents);
    }

    private void TrimRetiredContexts()
    {
        var maxRetiredContexts = Math.Clamp(PluginState.Config.Diagnostics.MaxRetiredPluginContexts, 20, 2000);
        if (_retiredPluginContexts.Count <= maxRetiredContexts)
        {
            return;
        }

        var removeCount = _retiredPluginContexts.Count - maxRetiredContexts;
        foreach (var collectedContext in _retiredPluginContexts
                     .Where(context => !context.AssemblyLoadContextAlive)
                     .OrderBy(context => context.RetiredUtc)
                     .Take(removeCount)
                     .ToArray())
        {
            _retiredPluginContexts.Remove(collectedContext);
            removeCount--;
            if (removeCount == 0)
            {
                return;
            }
        }

        if (removeCount > 0)
        {
            _retiredPluginContexts.RemoveRange(0, removeCount);
        }
    }

    private int NextGeneration(string internalName)
    {
        if (!_nextContextGenerationByInternalName.TryGetValue(internalName, out var generation))
        {
            generation = 1;
        }

        _nextContextGenerationByInternalName[internalName] = generation + 1;
        return generation;
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private ProfileAssembly[] GetProfilingAssemblies(string? plugin)
    {
        var contexts = new Dictionary<AssemblyLoadContext, string>(ReferenceEqualityComparer.Instance);
        lock (_gate)
        {
            foreach (var (name, selected) in _activePluginContexts)
            {
                if ((plugin is null || name.Equals(plugin, StringComparison.OrdinalIgnoreCase)) &&
                    selected.ContextReference?.TryGetTarget(out var context) == true)
                    contexts.TryAdd(context, name);
            }
        }

        var targets = new List<ProfileAssembly>();
        foreach (var (context, name) in contexts)
        {
            if (Service.PluginInterface.GetPlugin(context) is not { IsLoaded: true } loaded ||
                !loaded.InternalName.Equals(name, StringComparison.OrdinalIgnoreCase))
                continue;

            foreach (var assembly in SafeGetAssemblies(context))
            {
                if (!assembly.IsDynamic && assembly.FullName is { } identity)
                    targets.Add(new ProfileAssembly(name, identity, RuntimeModuleId.Read(assembly.ManifestModule)));
            }
        }
        return targets.ToArray();
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

    private static string GetAssemblyDisplayName(Assembly assembly)
    {
        var assemblyName = assembly.GetName();
        return assemblyName.Version is null
                   ? assemblyName.Name ?? assembly.FullName ?? "<unknown>"
                   : $"{assemblyName.Name} {assemblyName.Version}";
    }

    private static int GetContextId(AssemblyLoadContext context)
        => RuntimeHelpers.GetHashCode(context);

}

internal static class EnumerableExtensions
{
    public static IEnumerable<T> WhereNotNull<T>(this IEnumerable<T?> source)
        where T : class
    {
        foreach (var item in source)
        {
            if (item is not null)
            {
                yield return item;
            }
        }
    }
}
