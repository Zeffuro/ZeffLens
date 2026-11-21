using System;

namespace ZeffLens.Diagnostics;

public enum AddonPhase
{
    Setup,
    Show,
    Update,
    Draw,
    RequestedUpdate,
    Refresh,
    ReceiveEvent,
}

public sealed record AddonPhaseTiming(
    AddonPhase Phase, int Calls, double TotalMs, double P95Ms, double MaxMs, int PercentileSamples)
{
    public double AverageMs => TotalMs / Calls;
}

public readonly record struct AddonTimingSpike(AddonPhase Phase, double Seconds, double DurationMs);

public sealed record AddonNativeSnapshot(int LoadedInstances, int VisibleInstances, int NodeListEntries);

public sealed record AddonTiming(
    string Name, DateTime StartedUtc, double Seconds,
    AddonPhaseTiming[] Phases, AddonTimingSpike[] Slowest, int UnpairedEvents)
{
    public AddonNativeSnapshot? Before { get; init; }
    public AddonNativeSnapshot? After { get; init; }
}
