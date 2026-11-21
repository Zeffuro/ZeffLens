using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ZeffLens.Diagnostics;

namespace ZeffLens;

public sealed partial class MemoryDiagnosticsService
{
    private ReloadComparison? _reloadComparison;
    private string? _reloadError;
    private volatile ReloadCaptureRequest? _reloadRequest;
    private volatile WatchedLoad? _watchedLoad;
    private bool _watchingReload;
    private bool _watchingAutomaticReload;
    private volatile bool _reloadInProgress;
    private int _pluginChangeRevision;
    private readonly List<ReloadRun> _reloadRuns = new();
    private ReloadRun[]? _reloadRunView;

    internal bool ReloadInProgress => _reloadInProgress;

    internal ReloadRun[] GetReloadRuns()
    {
        lock (_gate)
            return _reloadRunView ??= _reloadRuns.ToArray();
    }

    internal (ReloadComparison? Comparison, bool Pending, string? Error, bool Watching, bool Settling, bool Automatic) GetReloadView()
    {
        lock (_gate)
            return (_reloadComparison, _reloadRequest is not null, _reloadError, _watchingReload, _watchedLoad is not null, _watchingAutomaticReload);
    }

    internal bool RequestReloadCapture(ReloadStage stage, string? plugin = null, bool watchAfter = false, bool reloadPlugin = false)
    {
        lock (_gate)
        {
            if (_disposed || _reloadRequest is not null || _reloadInProgress)
                return false;

            var target = stage == ReloadStage.Before ? plugin : _reloadComparison?.Plugin;
            if (string.IsNullOrWhiteSpace(target))
                return false;

            _reloadError = null;
            _reloadRequest = new ReloadCaptureRequest(stage, target, _reloadComparison, WatchAfter: watchAfter, ReloadPlugin: reloadPlugin);
            return true;
        }
    }

    internal void ResetReloadComparison()
    {
        lock (_gate)
        {
            _reloadComparison = null;
            _reloadRequest = null;
            _reloadError = null;
            ClearReloadWatch();
        }
    }

    internal void CancelReloadCapture()
    {
        lock (_gate)
        {
            if (_reloadRequest?.ExpectedLoad is not null)
                ClearReloadWatch();
            _reloadRequest = null;
        }
    }

    internal void StopReloadWatch()
    {
        lock (_gate)
        {
            ClearReloadWatch();
            if (_reloadRequest?.ExpectedLoad is not null)
                _reloadRequest = null;
        }
    }

    private void ClearReloadWatch()
    {
        _watchingReload = false;
        _watchingAutomaticReload = false;
        _watchedLoad = null;
    }

    private void ObserveWatchedLoad(PluginContextObservations observations, int delayMs)
    {
        if (!_watchingReload || _reloadComparison is not { Before.Active: { } before } comparison)
            return;

        if (!_activePluginContexts.TryGetValue(comparison.Plugin, out var current) ||
            (current.Generation == before.Context.Generation && current.ContextId == before.Context.ContextId) ||
            !observations.PluginContexts.Any(context => context.IsLoaded && current.IsSameContext(context.Context)))
        {
            _watchedLoad = null;
            return;
        }

        if (_watchedLoad is { } watched && watched.Generation == current.Generation && watched.ContextId == current.ContextId)
            return;

        var due = Stopwatch.GetTimestamp() + (long)(delayMs / 1000d * Stopwatch.Frequency);
        _watchedLoad = new WatchedLoad(current.Generation, current.ContextId, due);
    }

    private void QueueWatchedCapture(WatchedLoad watched)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(_watchedLoad, watched) || _reloadRequest is not null || _reloadInProgress ||
                _reloadComparison is not { } comparison || !_pendingChanges.IsEmpty)
                return;

            _reloadError = null;
            _reloadRequest = new ReloadCaptureRequest(ReloadStage.After, comparison.Plugin, comparison, ExpectedLoad: watched);
        }
    }

    private void RunReloadCapture(ReloadCaptureRequest request)
    {
        var revision = Volatile.Read(ref _pluginChangeRevision);
        ReloadCheckpoint? checkpoint = null;
        string? error;
        try
        {
            (checkpoint, error) = CaptureReloadCheckpoint(request);
        }
        catch (Exception ex)
        {
            Service.Logger.Warning(ex, "Reload comparison capture failed.");
            error = "Capture failed. See the Dalamud log.";
        }

        lock (_gate)
        {
            if (_disposed || !ReferenceEquals(_reloadRequest, request))
                return;

            if (request.ExpectedLoad is not null)
                ClearReloadWatch();

            if (revision != Volatile.Read(ref _pluginChangeRevision))
                _reloadError = "Plugin list changed during capture. Try again.";
            else if (checkpoint is null)
                _reloadError = error;
            else
            {
                _reloadComparison = request.Stage switch
                {
                    ReloadStage.Before => new ReloadComparison(request.Plugin, checkpoint, null, null),
                    ReloadStage.Unloaded => request.Comparison! with { Unloaded = checkpoint },
                    _ => request.Comparison! with { After = checkpoint },
                };
                if (request.Stage == ReloadStage.After)
                    RememberReloadRun(_reloadComparison);
                if (request.Stage != ReloadStage.Unloaded)
                {
                    ClearReloadWatch();
                    _watchingReload = request.Stage == ReloadStage.Before && (request.WatchAfter || request.ReloadPlugin);
                    _watchingAutomaticReload = _watchingReload && request.ReloadPlugin;
                }

                if (request.ReloadPlugin)
                {
                    _reloadInProgress = true;
                    _ = Task.Run(() => ReloadPluginAsync(request.Plugin, revision));
                }
            }

            _reloadRequest = null;
        }
    }

    private void RememberReloadRun(ReloadComparison comparison)
    {
        if (comparison.Before.Active is not { } before || comparison.After is not { Active: { } active } after)
            return;

        var resources = after.Original?.Resources;
        var run = new ReloadRun(comparison.Plugin, comparison.Before.Memory.Id, after.Memory.TimestampUtc,
            before.Context.Generation, active.Context.Generation, after.RetiredContextsAlive, after.Original?.Context.AssemblyLoadContextAlive,
            resources is null ? null : new ReloadRunCount(resources.HookCount, resources.ReadHookRegistry),
            resources is null ? null : new ReloadRunCount(resources.KtkCreatedNodeCount, resources.ReadKamiRegistries.HasFlag(KamiRegistries.Nodes)),
            after.Memory.PrivateBytes, after.Memory.PrivateBytes - comparison.Before.Memory.PrivateBytes,
            after.OtherPluginChanges.Length != 0);

        if (_reloadRuns.Count != 0 && _reloadRuns[^1].BeforeSampleId == run.BeforeSampleId)
            _reloadRuns[^1] = run;
        else
        {
            _reloadRuns.Add(run);
            if (_reloadRuns.Count > 64)
                _reloadRuns.RemoveAt(0);
        }
        _reloadRunView = null;
    }

    private async Task ReloadPluginAsync(string plugin, int revision)
    {
        var pluginInterface = Service.PluginInterface;
        var logger = Service.Logger;
        string? error = null;
        try
        {
            if (_disposed)
                return;
            if (revision != Volatile.Read(ref _pluginChangeRevision))
            {
                error = "Plugin list changed before the reload. Try again.";
                return;
            }
            await DalamudPluginReload.RunAsync(pluginInterface.InstalledPlugins, plugin, pluginInterface.InternalName).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger?.Warning(ex, "Could not reload {Plugin} through Dalamud.", plugin);
            error = $"Could not reload {plugin}. See the Dalamud log or reload it from the installer.";
        }
        finally
        {
            lock (_gate)
            {
                _reloadInProgress = false;
                if (!_disposed && error is not null)
                {
                    ClearReloadWatch();
                    _reloadError = error;
                }
            }
        }
    }

    private (ReloadCheckpoint? Checkpoint, string? Error) CaptureReloadCheckpoint(ReloadCaptureRequest request)
    {
        PluginContextSnapshot? active;
        PluginContextSnapshot? original;
        ProbeTarget? activeTarget;
        ProbeTarget? originalTarget;
        MemorySnapshot memory;
        int eventId;
        int? retiredAlive;
        string[] otherChanges;

        lock (_gate)
        {
            ThrowIfDisposed();
            if (!ReferenceEquals(_reloadRequest, request))
                return (null, null);

            var observations = ObservePluginContexts(includeAssemblyNames: false);
            if (observations.AssemblyLoadContextCount == 0)
                return (null, "Could not read the plugin contexts.");

            var timestamp = DateTime.UtcNow;
            ApplyObservations(timestamp, observations);
            var observed = observations.PluginContexts.FirstOrDefault(context => context.IsLoaded &&
                context.InternalName.Equals(request.Plugin, StringComparison.OrdinalIgnoreCase));
            var tracked = observed is null ? null : _activePluginContexts.GetValueOrDefault(observed.InternalName);
            active = tracked?.ToSnapshot();

            var comparison = request.Comparison;
            var before = comparison?.Before.Active;
            var retired = before is null ? null : _retiredPluginContexts.Find(context =>
                context.Generation == before.Context.Generation && context.ContextId == before.Context.ContextId &&
                context.InternalName.Equals(request.Plugin, StringComparison.OrdinalIgnoreCase));
            original = retired?.ToSnapshot();

            if (request.Stage == ReloadStage.Before && active is null)
                return (null, "Choose a loaded plugin to capture.");
            if (request.Stage != ReloadStage.Before && comparison?.Validate(request.Stage, active) is { } error)
                return (null, error);
            if (request.ExpectedLoad is { } expected &&
                (active is null || active.Generation != expected.Generation || active.ContextId != expected.ContextId))
                return (null, "The watched load changed. Use Capture after to check the current load.");
            if (request.Stage == ReloadStage.Unloaded && original is null)
                return (null, "Waiting for the old load to finish unloading.");

            memory = Sample($"Reload {request.Stage.ToString().ToLowerInvariant()}: {request.Plugin}");
            retiredAlive = request.Stage == ReloadStage.After
                ? _retiredPluginContexts.Count(context => context.InternalName.Equals(request.Plugin, StringComparison.OrdinalIgnoreCase) && context.AssemblyLoadContextAlive)
                : null;
            eventId = _nextEventId;
            activeTarget = tracked?.ToProbeTarget();
            originalTarget = retired?.ToProbeTarget();
            otherChanges = request.Stage != ReloadStage.Before && comparison is not null
                ? _events.Where(change => change.Id > comparison.Before.LastEventId)
                         .SelectMany(change => change.AffectedInternalNames)
                         .Where(name => !name.Equals(request.Plugin, StringComparison.OrdinalIgnoreCase))
                         .Distinct(StringComparer.OrdinalIgnoreCase)
                         .Order(StringComparer.OrdinalIgnoreCase)
                         .ToArray()
                : [];
        }

        ProbeTarget?[] candidates = request.Stage == ReloadStage.Before ? [activeTarget] : [activeTarget, originalTarget];
        var targets = candidates.WhereNotNull().ToArray();
        var probes = ResourceProbeService.Refresh(targets, memory.TimestampUtc, memory.Reason, focused: true);
        var errors = probes.Where(probe => probe.Generation == 0 && probe.InternalName == ProbeOwner.Global.InternalName)
                           .SelectMany(probe => probe.Errors)
                           .ToArray();

        return (new ReloadCheckpoint(memory, BuildGeneration(active, probes),
            request.Stage == ReloadStage.Before ? null : BuildGeneration(original, probes), errors, otherChanges)
        {
            LastEventId = eventId,
            RetiredContextsAlive = retiredAlive,
        }, null);
    }

    private static ReloadGeneration? BuildGeneration(PluginContextSnapshot? context, PluginResourceProbeSnapshot[] probes)
    {
        if (context is null)
            return null;

        var generation = new ReloadGeneration(context, null);
        return generation with { Resources = Array.Find(probes, generation.Matches) };
    }

    private sealed record WatchedLoad(int Generation, int ContextId, long Due);

    private sealed record ReloadCaptureRequest(ReloadStage Stage, string Plugin, ReloadComparison? Comparison,
        bool WatchAfter = false, WatchedLoad? ExpectedLoad = null, bool ReloadPlugin = false);
}
