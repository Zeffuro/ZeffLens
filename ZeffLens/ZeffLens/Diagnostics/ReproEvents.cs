using System;
using Dalamud.Game.Addon.Lifecycle;

namespace ZeffLens.Diagnostics;

public sealed record AddonLifecycleEntry(DateTime TimestampUtc, string AddonName, AddonEvent Event);

public sealed record CaptureMarker(double ElapsedSeconds, string? Note, AddonLifecycleEntry? Addon);

public readonly record struct GcObservation(int SampleIndex, int Gen0, int Gen1, int Gen2);
