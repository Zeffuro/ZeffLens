using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using ZeffLens.Diagnostics;

namespace ZeffLens;

public sealed partial class MemoryDiagnosticsService
{
    internal const int MemoryActionFollowUpSeconds = 5;
    private readonly object _memoryActionGate = new();
    private MemoryReleaseResult? _lastReleaseResult;
    private long _memoryActionFollowUpAt;
    private int _memoryActionPluginRevision;

    public MemoryReleaseResult? LastReleaseResult => Volatile.Read(ref _lastReleaseResult);

    internal bool CanRunMemoryAction => !Frames.IsRecording && !ManagedProfiles.IsRunning &&
                                        !AddonProfiles.IsRunning && !ReloadInProgress;

    public MemoryReleaseResult? ReleaseWorkingMemory(bool collect = true, bool? trim = null)
    {
        lock (_memoryActionGate)
        {
            ThrowIfDisposed();
            if (!CanRunMemoryAction)
                return null;

            Frames.NoteInterference();
            var settings = PluginState.Config.Diagnostics;
            var trimRequested = !collect || (trim ?? settings.TrimWorkingSetOnRelease);
            var action = collect ? trimRequested ? "GC and trim" : "full GC" : "trim";
            var revision = Volatile.Read(ref _pluginChangeRevision);
            var before = Sample($"Before {action}");
            var started = Stopwatch.GetTimestamp();

            if (collect)
                RunManagedCollection(settings.CompactLargeObjectHeapOnRelease);

            lock (_gate)
            {
                ThrowIfDisposed();
                var trimmed = trimRequested && EmptyWorkingSet(_process.Handle);
                var error = trimRequested && !trimmed ? Marshal.GetLastPInvokeError() : 0;
                var duration = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                var after = Sample($"Immediately after {action}", forcedGcJustRan: collect);
                var result = new MemoryReleaseResult(after.TimestampUtc, before, after, trimRequested, trimmed, error)
                {
                    Collected = collect,
                    ActionDurationMs = duration,
                };

                _lastReleaseResult = result;
                _memoryActionPluginRevision = revision;
                Volatile.Write(ref _memoryActionFollowUpAt, Stopwatch.GetTimestamp() + MemoryActionFollowUpSeconds * Stopwatch.Frequency);
                Service.Logger.Information("Memory action {Action} took {DurationMs:F1} ms. Private commit delta {CommitDelta}, total RAM delta {RamDelta}, managed heap delta {ManagedDelta}",
                    action, duration, DiagnosticFormatting.FormatSignedBytes(after.PrivateBytes - before.PrivateBytes),
                    DiagnosticFormatting.FormatSignedBytes(after.WorkingSetBytes - before.WorkingSetBytes),
                    DiagnosticFormatting.FormatSignedBytes(after.ManagedLiveBytes - before.ManagedLiveBytes));
                return result;
            }
        }
    }

    private bool CaptureMemoryFollowUp(long now)
    {
        var due = Volatile.Read(ref _memoryActionFollowUpAt);
        if (due == 0 || now < due)
            return false;

        lock (_gate)
        {
            if (_disposed || due != _memoryActionFollowUpAt || _lastReleaseResult is not { } result)
                return false;

            _lastReleaseResult = result with
            {
                FollowUp = Sample("Memory action follow-up"),
                PluginListChanged = Volatile.Read(ref _pluginChangeRevision) != _memoryActionPluginRevision,
            };
            Volatile.Write(ref _memoryActionFollowUpAt, 0);
            return true;
        }
    }

    [DllImport("psapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EmptyWorkingSet(IntPtr processHandle);
}
