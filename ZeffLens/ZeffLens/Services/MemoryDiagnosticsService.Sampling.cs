using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime;
using System.Threading;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using ZeffLens.Diagnostics;

namespace ZeffLens;

public sealed partial class MemoryDiagnosticsService
{
    private readonly ConcurrentQueue<PluginChange> _pendingChanges = new();
    private long _lastAutoSample;
    private long _settleAt;

    private void OnFrameworkUpdate(IFramework _)
    {
        if (_disposed)
            return;

        if (AddonProfiles.IsRunning)
        {
            if (AddonProfiles.Tick())
                Frames.NoteInterference();
            return;
        }

        if (!_pendingChanges.IsEmpty)
        {
            if (Frames.IsCapturing)
                Frames.NoteInterference();
            else
                ReadPluginChanges();
        }

        if (Frames.IsRecording)
        {
            Frames.Tick();
            return;
        }

        if (ManagedProfiles.IsRunning)
            return;

        if (_watchedLoad is { } watched && Stopwatch.GetTimestamp() >= watched.Due)
            QueueWatchedCapture(watched);

        if (_reloadRequest is { } request)
        {
            RunReloadCapture(request);
            return;
        }

        if (Interlocked.Exchange(ref _requestedProbe, null) is { } reason)
        {
            try
            {
                RefreshResourceProbes(reason);
            }
            catch (Exception ex)
            {
                Service.Logger.Warning(ex, "Resource probe failed.");
            }
        }

        var now = Stopwatch.GetTimestamp();
        if (!ReloadInProgress && CaptureMemoryFollowUp(now))
        {
            _lastAutoSample = now;
            return;
        }
        var settings = PluginState.Config.Diagnostics;
        if (_settleAt != 0 && now >= _settleAt)
        {
            _settleAt = 0;
            _lastAutoSample = now;
            Sample("After plugin changes");
        }
        else if (settings.AutoSampleEnabled &&
                 Stopwatch.GetElapsedTime(_lastAutoSample, now).TotalSeconds >= Math.Clamp(settings.AutoSampleIntervalSeconds, 1, 60))
        {
            _lastAutoSample = now;
            Sample("Auto sample");
        }
    }

    private void OnActivePluginsChanged(IActivePluginsChangedEventArgs args)
    {
        if (_disposed)
            return;

        _pendingChanges.Enqueue(new PluginChange(args.Kind, args.AffectedInternalNames.ToArray(), DateTime.UtcNow));
        Interlocked.Increment(ref _pluginChangeRevision);
    }

    private void ReadPluginChanges()
    {
        var changes = new List<PluginChange>();
        while (_pendingChanges.TryDequeue(out var change))
            changes.Add(change);

        if (changes.Count == 0)
            return;

        lock (_gate)
        {
            var previous = _snapshots.Count > 0 ? _snapshots[^1] : null;
            var observations = ObservePluginContexts(includeAssemblyNames: false);
            foreach (var change in changes)
            {
                if (change.Kind != PluginListInvalidationKind.Unloaded)
                    continue;

                foreach (var name in change.Names)
                {
                    if (!_activePluginContexts.TryGetValue(name, out var tracked))
                        continue;

                    var observedStillActive = observations.PluginContexts.Any(context => context.IsLoaded &&
                        context.InternalName.Equals(name, StringComparison.OrdinalIgnoreCase) && tracked.IsSameContext(context.Context));
                    if (!observedStillActive)
                        RetireActiveContext(name, change.TimestampUtc, "Plugin unloaded");
                }
            }

            var snapshot = Sample("Plugin changes", observations: observations);
            foreach (var change in changes)
            {
                _events.Add(new(++_nextEventId, change.TimestampUtc, change.Kind, change.Names, snapshot, previous));
            }

            TrimEvents();
            _eventView = null;
            var delay = Math.Clamp(PluginState.Config.Diagnostics.SettledSnapshotDelayMs, 0, 30000);
            _settleAt = delay == 0 ? 0 : Stopwatch.GetTimestamp() + (long)(delay / 1000d * Stopwatch.Frequency);
            ObserveWatchedLoad(observations, delay);
        }
    }

    private static void RunManagedCollection(bool compactLargeObjectHeap)
    {
        if (compactLargeObjectHeap)
        {
            GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
        }

        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
    }

    private MemorySnapshot CaptureProcessSnapshot(
        string reason,
        DateTime timestamp,
        int assemblyLoadContextCount,
        int loadedPluginContextCount)
    {
        var gcMemoryInfo = GC.GetGCMemoryInfo();
        var (privateBytes, workingSetBytes, privateWorkingSetBytes) = WindowsProcessMemory.Read(_process);
        var managedLiveBytes = GC.GetTotalMemory(forceFullCollection: false);

        return new MemorySnapshot(
            ++_nextSnapshotId,
            timestamp,
            reason,
            privateBytes,
            workingSetBytes,
            managedLiveBytes,
            GC.GetTotalAllocatedBytes(precise: false),
            gcMemoryInfo.HeapSizeBytes,
            gcMemoryInfo.FragmentedBytes,
            gcMemoryInfo.TotalCommittedBytes,
            gcMemoryInfo.MemoryLoadBytes,
            gcMemoryInfo.HighMemoryLoadThresholdBytes,
            Math.Max(0, privateBytes - managedLiveBytes),
            GC.CollectionCount(0),
            GC.CollectionCount(1),
            GC.CollectionCount(2),
            assemblyLoadContextCount,
            loadedPluginContextCount)
        {
            PrivateWorkingSetBytes = privateWorkingSetBytes,
        };
    }

    private sealed record PluginChange(PluginListInvalidationKind Kind, string[] Names, DateTime TimestampUtc);
}
