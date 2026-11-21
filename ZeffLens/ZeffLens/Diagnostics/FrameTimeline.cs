using System;
using System.Linq;

namespace ZeffLens.Diagnostics;

internal sealed class FrameTimeline
{
    public FrameTimeline(FrameCapture capture, double duration, int bucketCount)
    {
        Capture = capture;
        Duration = duration;
        Peaks = new int[bucketCount];
        Array.Fill(Peaks, -1);
        IntervalEnds = new double[capture.IntervalsMs.Length];
        GcTimes = new double[capture.GcObservations.Length];
        GcBuckets = new bool[bucketCount];

        var elapsed = 0d;
        for (var i = 0; i < IntervalEnds.Length; i++)
        {
            var bucket = BucketAt(elapsed);
            var current = Peaks[bucket];
            var interval = capture.IntervalsMs[i];
            if (current < 0 || interval > capture.IntervalsMs[current])
                Peaks[bucket] = i;

            elapsed += interval / 1000d;
            IntervalEnds[i] = i == IntervalEnds.Length - 1 ? Math.Max(Capture.DurationSeconds, elapsed) : elapsed;
        }

        for (var i = 0; i < GcTimes.Length; i++)
        {
            var time = IntervalEnds[capture.GcObservations[i].SampleIndex];
            GcTimes[i] = time;
            GcBuckets[BucketAt(time)] = true;
        }

        Slowest = Enumerable.Range(0, IntervalEnds.Length).OrderByDescending(i => capture.IntervalsMs[i]).Take(5).ToArray();
    }

    public FrameCapture Capture { get; }
    public double Duration { get; }
    public int[] Peaks { get; }
    public double[] IntervalEnds { get; }
    public int[] Slowest { get; }
    public double[] GcTimes { get; }
    public bool[] GcBuckets { get; }

    public int BucketAt(double elapsed)
        => Math.Clamp((int)(elapsed * Peaks.Length / Math.Max(0.001, Duration)), 0, Peaks.Length - 1);

    public double StartOf(int sample) => sample == 0 ? 0 : IntervalEnds[sample - 1];

    public int SampleAt(double seconds)
    {
        var index = Array.BinarySearch(IntervalEnds, seconds);
        return Math.Min(index >= 0 ? index : ~index, IntervalEnds.Length - 1);
    }

    public int FirstGcAt(double seconds)
    {
        var index = Array.BinarySearch(GcTimes, seconds);
        return index >= 0 ? index : ~index;
    }
}
