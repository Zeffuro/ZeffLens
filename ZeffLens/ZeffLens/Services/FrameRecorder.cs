using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using ZeffLens.Diagnostics;

namespace ZeffLens;

internal sealed class FrameRecorder(Func<NativeFrameReading?>? readNativeFrame = null, Func<GraphicsMemorySnapshot?>? readGraphicsMemory = null)
{
    private const int LeadInSeconds = 3;
    private const int MaxSamples = 32768;
    private const int MaxMarkers = 256;

    private readonly object _gate = new();
    private volatile Recording? _recording;
    private FrameCapture? _reference;
    private FrameCapture? _candidate;

    public bool IsRecording => _recording is not null;
    public bool IsCapturing => _recording?.HasStarted == true;

    public bool Start(bool reference, string label, int seconds, bool graphicsMemory = false)
    {
        lock (_gate)
        {
            if (_recording is not null)
                return false;

            _recording = new Recording(
                reference, label.Trim(), Math.Clamp(seconds, 5, 60), Stopwatch.GetTimestamp(), readNativeFrame is not null, graphicsMemory);
            return true;
        }
    }

    public bool Cancel()
    {
        lock (_gate)
        {
            if (_recording is null)
                return false;

            _recording = null;
            return true;
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _recording = null;
            _reference = null;
            _candidate = null;
        }
    }

    public (FrameCapture? Reference, FrameCapture? Candidate, double? ProgressSeconds) Read()
    {
        lock (_gate)
        {
            var progress = _recording is { } recording
                ? recording.HasStarted
                    ? recording.ElapsedMs / 1000
                    : Math.Min(0, Stopwatch.GetElapsedTime(recording.RequestedAt).TotalSeconds - LeadInSeconds)
                : (double?)null;

            return (_reference, _candidate, progress);
        }
    }

    public void NoteInterference()
    {
        if (_recording is null)
            return;

        lock (_gate)
        {
            if (_recording is { HasStarted: true } recording)
                recording.Interrupted = true;
        }
    }

    public bool Mark(string note)
    {
        note = note.Trim();
        return RecordMarker(note.Length == 0 ? "Mark" : note[..Math.Min(note.Length, 160)], null, Stopwatch.GetTimestamp());
    }

    public void MarkAddon(AddonLifecycleEntry entry, long timestamp)
        => RecordMarker(null, entry, timestamp);

    private bool RecordMarker(string? note, AddonLifecycleEntry? entry, long timestamp)
    {
        if (_recording is null)
            return false;

        lock (_gate)
        {
            if (_recording is not { HasStarted: true } recording || timestamp < recording.StartedAt)
                return false;

            if (recording.Markers.Count == MaxMarkers)
            {
                recording.DroppedMarkers++;
                return false;
            }

            var seconds = Stopwatch.GetElapsedTime(recording.StartedAt, timestamp).TotalSeconds;
            recording.Markers.Add(new CaptureMarker(seconds, note, entry));
            return true;
        }
    }

    public void Tick()
    {
        lock (_gate)
        {
            if (_recording is not { } recording)
                return;

            var now = Stopwatch.GetTimestamp();
            if (!recording.HasStarted)
            {
                if (Stopwatch.GetElapsedTime(recording.RequestedAt, now).TotalSeconds < LeadInSeconds)
                    return;

                if (recording.CaptureGraphicsMemory)
                {
                    recording.GraphicsStart = readGraphicsMemory?.Invoke();
                    now = Stopwatch.GetTimestamp();
                }
                recording.StartedUtc = DateTime.UtcNow;
                recording.StartAllocatedBytes = GC.GetTotalAllocatedBytes(precise: false);
                recording.StartCollections = recording.LastCollections = CollectionCounts.Read();
                recording.StartedAt = now;
                recording.LastTickAt = now;
                return;
            }

            if (now <= recording.LastTickAt)
                return;

            var sampleIndex = recording.Count++;
            recording.Samples[sampleIndex] = (float)Stopwatch.GetElapsedTime(recording.LastTickAt, now).TotalMilliseconds;
            // Skip the game delta that may include the DXGI read.
            if (recording.NativeFrames.Length != 0 && (sampleIndex > 0 || !recording.CaptureGraphicsMemory) &&
                readNativeFrame?.Invoke() is { } native && native.FrameNumber != recording.LastNativeFrame)
            {
                recording.LastNativeFrame = native.FrameNumber;
                recording.NativeFrames[sampleIndex] = native;
                recording.HasNativeFrames = true;
            }
            recording.LastTickAt = now;
            recording.ElapsedMs = Stopwatch.GetElapsedTime(recording.StartedAt, now).TotalMilliseconds;

            var collections = CollectionCounts.Read();
            var last = recording.LastCollections;
            if (collections != last)
            {
                recording.GcObservations.Add(new(recording.Count - 1, collections.Gen0 - last.Gen0,
                    collections.Gen1 - last.Gen1, collections.Gen2 - last.Gen2));
                recording.LastCollections = collections;
            }

            var limitReached = recording.Count == recording.Samples.Length && recording.ElapsedMs < recording.Seconds * 1000;
            if (!limitReached && recording.ElapsedMs < recording.Seconds * 1000)
                return;

            var start = recording.StartCollections;
            var allocatedBytes = GC.GetTotalAllocatedBytes(precise: false) - recording.StartAllocatedBytes;
            var capture = FrameCapture.Summarize(
                recording.Label, recording.StartedUtc, recording.Seconds, recording.ElapsedMs / 1000, limitReached, recording.Interrupted,
                recording.Samples.AsSpan(0, recording.Count).ToArray(),
                allocatedBytes, collections.Gen0 - start.Gen0, collections.Gen1 - start.Gen1, collections.Gen2 - start.Gen2,
                recording.Markers.OrderBy(marker => marker.ElapsedSeconds).ToArray(),
                recording.DroppedMarkers, recording.GcObservations.ToArray()) with
            {
                NativeFrames = recording.HasNativeFrames ? recording.NativeFrames.AsSpan(0, recording.Count).ToArray() : [],
                GraphicsMemory = recording.CaptureGraphicsMemory ? new(recording.GraphicsStart, readGraphicsMemory?.Invoke()) : null,
            };

            if (recording.Reference)
                _reference = capture;
            else
                _candidate = capture;

            _recording = null;
        }
    }

    private sealed class Recording(bool reference, string label, int seconds, long requestedAt, bool nativeFrames, bool captureGraphicsMemory)
    {
        public readonly bool Reference = reference;
        public readonly string Label = label;
        public readonly int Seconds = seconds;
        public readonly long RequestedAt = requestedAt;
        public readonly float[] Samples = new float[MaxSamples];
        public readonly NativeFrameReading?[] NativeFrames = nativeFrames ? new NativeFrameReading?[MaxSamples] : [];
        public readonly bool CaptureGraphicsMemory = captureGraphicsMemory;
        public readonly List<CaptureMarker> Markers = new(32);
        public readonly List<GcObservation> GcObservations = new(32);

        public DateTime StartedUtc;
        public GraphicsMemorySnapshot? GraphicsStart;
        public uint? LastNativeFrame;
        public bool HasNativeFrames;
        public long StartAllocatedBytes;
        public CollectionCounts StartCollections;
        public CollectionCounts LastCollections;
        public long StartedAt;
        public long LastTickAt;
        public double ElapsedMs;
        public int Count;
        public int DroppedMarkers;
        public bool Interrupted;
        public bool HasStarted => StartedUtc != default;
    }

    private readonly record struct CollectionCounts(int Gen0, int Gen1, int Gen2)
    {
        public static CollectionCounts Read()
            => new(GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2));
    }
}
