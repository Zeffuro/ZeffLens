using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Plugin.Services;
using ZeffLens.Diagnostics;

namespace ZeffLens;

internal sealed class AddonWatch(IAddonLifecycle lifecycle, FrameRecorder frames) : IDisposable
{
    private const int HistoryLimit = 256;
    private static readonly AddonEvent[] Events =
        [AddonEvent.PostSetup, AddonEvent.PostShow, AddonEvent.PostHide, AddonEvent.PreFinalize];

    private readonly object _gate = new();
    private readonly Queue<AddonLifecycleEntry> _entries = new(HistoryLimit);
    private AddonLifecycleEntry[]? _view;
    private Watch? _active;
    private bool _disposed;

    public bool Start(string name)
    {
        name = name.Trim();
        if (!IsValidName(name))
            return false;

        lock (_gate)
        {
            if (_disposed)
                return false;

            if (string.Equals(_active?.Name, name, StringComparison.Ordinal))
                return true;

            StopWatch();
            var watch = new Watch(name, Record);
            _active = watch;
            try
            {
                foreach (var type in Events)
                    lifecycle.RegisterListener(type, name, watch.Handler);
            }
            catch
            {
                StopWatch();
                throw;
            }

            return true;
        }
    }

    internal static bool IsValidName(string name)
        => name.Length is > 0 and <= 64 && !name.Any(ch => char.IsWhiteSpace(ch) || ch == '*');

    public void Stop()
    {
        lock (_gate)
            StopWatch();
    }

    public (string? Name, AddonLifecycleEntry[] Entries) Read()
    {
        lock (_gate)
            return (_active?.Name, _view ??= _entries.ToArray());
    }

    public void Clear()
    {
        lock (_gate)
        {
            _entries.Clear();
            _view = null;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            StopWatch();
        }
    }

    private void StopWatch()
    {
        var watch = _active;
        _active = null;
        if (watch is not null)
            lifecycle.UnregisterListener(watch.Handler);
    }

    private void Record(Watch watch, AddonEvent type)
    {
        var now = Stopwatch.GetTimestamp();
        lock (_gate)
        {
            if (!ReferenceEquals(_active, watch))
                return;

            var entry = new AddonLifecycleEntry(DateTime.UtcNow, watch.Name, type);
            if (_entries.Count == HistoryLimit)
                _entries.Dequeue();
            _entries.Enqueue(entry);
            _view = null;
            frames.MarkAddon(entry, now);
        }
    }

    private sealed class Watch
    {
        public Watch(string name, Action<Watch, AddonEvent> record)
        {
            Name = name;
            Handler = (type, _) => record(this, type);
        }

        public string Name { get; }
        public IAddonLifecycle.AddonEventDelegate Handler { get; }
    }
}
