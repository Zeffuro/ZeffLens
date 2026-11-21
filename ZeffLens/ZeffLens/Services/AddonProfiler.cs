using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Plugin.Services;
using ZeffLens.Diagnostics;

namespace ZeffLens;

internal sealed class AddonProfiler(IAddonLifecycle lifecycle, Action<Exception> logError,
    Func<string, AddonNativeSnapshot?> captureNative) : IDisposable
{
    private static readonly (AddonPhase Phase, AddonEvent Before, AddonEvent After)[] Events =
    [
        (AddonPhase.Setup, AddonEvent.PreSetup, AddonEvent.PostSetup),
        (AddonPhase.Show, AddonEvent.PreShow, AddonEvent.PostShow),
        (AddonPhase.Update, AddonEvent.PreUpdate, AddonEvent.PostUpdate),
        (AddonPhase.Draw, AddonEvent.PreDraw, AddonEvent.PostDraw),
        (AddonPhase.RequestedUpdate, AddonEvent.PreRequestedUpdate, AddonEvent.PostRequestedUpdate),
        (AddonPhase.Refresh, AddonEvent.PreRefresh, AddonEvent.PostRefresh),
        (AddonPhase.ReceiveEvent, AddonEvent.PreReceiveEvent, AddonEvent.PostReceiveEvent),
    ];

    private readonly object _gate = new();
    private volatile Session? _active;
    private AddonTiming? _last;
    private string? _error;
    private bool _disposed;

    public bool IsRunning => _active is not null;

    public bool Start(string name, int seconds)
    {
        name = name.Trim();
        if (!AddonWatch.IsValidName(name))
            return false;

        lock (_gate)
        {
            if (_disposed || _active is not null)
                return false;

            var session = new Session(name, Math.Clamp(seconds, 5, 60), Events.Length + 1);
            _error = null;
            try
            {
                foreach (var (phase, before, after) in Events)
                {
                    IAddonLifecycle.AddonEventDelegate callback = (type, args) =>
                        Record(session, phase, type == before, args.Addon);
                    session.Handlers.Add(callback);
                    lifecycle.RegisterListener(before, name, callback);
                    lifecycle.RegisterListener(after, name, callback);
                }

                IAddonLifecycle.AddonEventDelegate finalize = (_, args) => Forget(session, args.Addon);
                session.Handlers.Add(finalize);
                lifecycle.RegisterListener(AddonEvent.PreFinalize, name, finalize);
            }
            catch (Exception ex)
            {
                Unregister(session);
                logError(ex);
                _error = "Could not start addon profiling. See the Dalamud log.";
                return false;
            }

            session.Before = CaptureNative(name);
            session.StartedUtc = DateTime.UtcNow;
            session.StartedAt = Stopwatch.GetTimestamp();
            session.Due = session.StartedAt + session.Seconds * Stopwatch.Frequency;
            _active = session;
            return true;
        }
    }

    public (string? ActiveName, double Remaining, AddonTiming? Last, string? Error) Read()
    {
        lock (_gate)
        {
            var remaining = _active is { } active
                ? Math.Max(0, Stopwatch.GetElapsedTime(Stopwatch.GetTimestamp(), active.Due).TotalSeconds)
                : 0;
            return (_active?.Name, remaining, _last, _error);
        }
    }

    public bool Tick()
    {
        if (_active is { } session && Stopwatch.GetTimestamp() >= session.Due)
        {
            lock (_gate)
            {
                if (ReferenceEquals(_active, session))
                {
                    Finish(session, keep: true);
                    return true;
                }
            }
        }
        return false;
    }

    public void Stop()
    {
        lock (_gate)
        {
            if (_active is { } session)
                Finish(session, keep: true);
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            if (_active is { } session)
                Finish(session, keep: false);
            _last = null;
            _error = null;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            Clear();
        }
    }

    private void Finish(Session session, bool keep)
    {
        var ended = Math.Min(Stopwatch.GetTimestamp(), session.Due);
        _active = null;
        Unregister(session);
        if (keep)
            _last = session.Summarize(ended, CaptureNative(session.Name));
    }

    private AddonNativeSnapshot? CaptureNative(string name)
    {
        try
        {
            return captureNative(name);
        }
        catch (Exception ex)
        {
            logError(ex);
            return null;
        }
    }

    private void Unregister(Session session)
    {
        try
        {
            lifecycle.UnregisterListener(session.Handlers.ToArray());
        }
        catch (Exception ex)
        {
            logError(ex);
            _error = "Could not remove an addon listener. See the Dalamud log.";
        }
    }

    private void Record(Session session, AddonPhase phase, bool before, nint addon)
    {
        var now = Stopwatch.GetTimestamp();
        lock (_gate)
        {
            if (!ReferenceEquals(_active, session) || now >= session.Due || addon == 0)
                return;

            var key = new CallKey(phase, addon, Environment.CurrentManagedThreadId);
            if (before)
                session.Begin(key);
            else
                session.End(key, now);
        }
    }

    private void Forget(Session session, nint addon)
    {
        lock (_gate)
        {
            if (ReferenceEquals(_active, session))
                session.Forget(addon);
        }
    }

    private readonly record struct CallKey(AddonPhase Phase, nint Addon, int Thread);
    private readonly record struct PendingCall(CallKey Key, long StartedAt);
    private readonly record struct MeasuredCall(AddonPhase Phase, long StartedAt, long Duration);

    private sealed class Session(string name, int seconds, int handlerCount)
    {
        private readonly PendingCall[] _pending = new PendingCall[64];
        private readonly PhaseStats[] _phases = Events.Select(_ => new PhaseStats()).ToArray();
        private readonly PriorityQueue<MeasuredCall, long> _slowest = new(8);
        private int _pendingCount;
        private int _unpaired;

        public readonly List<IAddonLifecycle.AddonEventDelegate> Handlers = new(handlerCount);
        public readonly string Name = name;
        public readonly int Seconds = seconds;
        public AddonNativeSnapshot? Before;
        public DateTime StartedUtc;
        public long StartedAt;
        public long Due;

        public void Begin(CallKey key)
        {
            if (_pendingCount == _pending.Length)
            {
                _unpaired += _pendingCount;
                _pendingCount = 0;
            }
            _pending[_pendingCount++] = new PendingCall(key, Stopwatch.GetTimestamp());
        }

        public void End(CallKey key, long now)
        {
            for (var i = _pendingCount - 1; i >= 0; i--)
            {
                if (_pending[i].Key != key)
                    continue;

                var start = _pending[i].StartedAt;
                Remove(i);
                if (now < start)
                {
                    _unpaired++;
                    return;
                }

                var ticks = now - start;
                _phases[(int)key.Phase].Add(ticks);
                var call = new MeasuredCall(key.Phase, start, ticks);
                if (_slowest.Count < 8)
                    _slowest.Enqueue(call, ticks);
                else if (_slowest.TryPeek(out _, out var shortest) && ticks > shortest)
                    _slowest.DequeueEnqueue(call, ticks);
                return;
            }

            _unpaired++;
        }

        public void Forget(nint addon)
        {
            for (var i = _pendingCount - 1; i >= 0; i--)
            {
                if (_pending[i].Key.Addon != addon)
                    continue;
                Remove(i);
                _unpaired++;
            }
        }

        public AddonTiming Summarize(long ended, AddonNativeSnapshot? after)
        {
            var phases = Events.Where(item => _phases[(int)item.Phase].Count > 0)
                .Select(item => _phases[(int)item.Phase].Summarize(item.Phase))
                .OrderByDescending(item => item.TotalMs)
                .ToArray();
            var slowest = _slowest.UnorderedItems
                .OrderByDescending(item => item.Priority)
                .Select(item => new AddonTimingSpike(item.Element.Phase,
                    Stopwatch.GetElapsedTime(StartedAt, item.Element.StartedAt).TotalSeconds, Milliseconds(item.Element.Duration)))
                .ToArray();
            return new AddonTiming(Name, StartedUtc, Stopwatch.GetElapsedTime(StartedAt, ended).TotalSeconds,
                phases, slowest, _unpaired + _pendingCount)
            {
                Before = Before,
                After = after,
            };
        }

        private void Remove(int index)
        {
            _pendingCount--;
            _pending.AsSpan(index + 1, _pendingCount - index).CopyTo(_pending.AsSpan(index));
        }
    }

    private static double Milliseconds(long ticks) => ticks * (1000d / Stopwatch.Frequency);

    private sealed class PhaseStats
    {
        private readonly long[] _recent = new long[4096];
        private long _total;
        private long _max;
        public int Count { get; private set; }

        public void Add(long ticks)
        {
            _recent[Count++ % _recent.Length] = ticks;
            _total += ticks;
            _max = Math.Max(_max, ticks);
        }

        public AddonPhaseTiming Summarize(AddonPhase phase)
        {
            var sorted = _recent.AsSpan(0, Math.Min(_recent.Length, Count)).ToArray();
            Array.Sort(sorted);
            var p95 = sorted[(int)Math.Ceiling(sorted.Length * 0.95) - 1];
            return new AddonPhaseTiming(phase, Count, Milliseconds(_total), Milliseconds(p95), Milliseconds(_max), sorted.Length);
        }
    }
}
