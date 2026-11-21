using System;

namespace ZeffLens.Diagnostics;

public readonly record struct NativeFrameReading(uint FrameNumber, float DeltaMs);

public sealed record FrameCapture(
    string Label,
    DateTime StartedUtc,
    int TargetSeconds,
    double DurationSeconds,
    bool SampleLimitReached,
    bool Interrupted,
    float MedianMs,
    float P95Ms,
    float P99Ms,
    float MaxMs,
    int Over33Ms,
    int Over50Ms,
    long AllocatedBytes,
    int Gen0Collections,
    int Gen1Collections,
    int Gen2Collections,
    float[] IntervalsMs,
    CaptureMarker[] Markers,
    int DroppedMarkers,
    GcObservation[] GcObservations)
{
    public NativeFrameReading?[] NativeFrames { get; init; } = [];
    public GraphicsMemoryCapture? GraphicsMemory { get; init; }

    internal static FrameCapture Summarize(
        string label, DateTime startedUtc, int targetSeconds, double durationSeconds, bool limitReached, bool interrupted,
        float[] intervals, long allocatedBytes, int gen0Collections, int gen1Collections, int gen2Collections,
        CaptureMarker[] markers, int droppedMarkers, GcObservation[] gcObservations)
    {
        var sorted = (float[])intervals.Clone();
        Array.Sort(sorted);

        var over33Ms = 0;
        var over50Ms = 0;
        foreach (var interval in intervals)
        {
            if (interval > 33.333333f)
                over33Ms++;
            if (interval > 50)
                over50Ms++;
        }

        return new FrameCapture(
            label, startedUtc, targetSeconds, durationSeconds, limitReached, interrupted,
            Percentile(sorted, 0.50), Percentile(sorted, 0.95), Percentile(sorted, 0.99), sorted[^1],
            over33Ms, over50Ms, allocatedBytes, gen0Collections, gen1Collections, gen2Collections,
            intervals, markers, droppedMarkers, gcObservations);
    }

    private static float Percentile(float[] sorted, double percentile)
        => sorted[(int)Math.Ceiling(sorted.Length * percentile) - 1];
}
