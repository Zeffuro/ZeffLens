using System;

using Dalamud.Plugin;

namespace ZeffLens.Diagnostics;

public sealed record MemorySnapshot(
    int Id,
    DateTime TimestampUtc,
    string Reason,
    long PrivateBytes,
    long WorkingSetBytes,
    long ManagedLiveBytes,
    long ManagedAllocatedBytes,
    long GcHeapSizeBytes,
    long GcFragmentedBytes,
    long GcCommittedBytes,
    long GcMemoryLoadBytes,
    long GcHighMemoryLoadThresholdBytes,
    long EstimatedNativeBytes,
    int Gen0Collections,
    int Gen1Collections,
    int Gen2Collections,
    int AssemblyLoadContextCount,
    int LoadedPluginContextCount)
{
    public long? PrivateWorkingSetBytes { get; init; }
}

public sealed record PluginMemoryEvent(
    int Id,
    DateTime TimestampUtc,
    PluginListInvalidationKind Kind,
    string[] AffectedInternalNames,
    MemorySnapshot Snapshot,
    MemorySnapshot? PreviousSnapshot);

public sealed record PluginContextSnapshot(
    string InternalName,
    string Name,
    int Generation,
    bool IsLoaded,
    bool IsRetired,
    bool IsDev,
    bool IsTesting,
    bool IsThirdParty,
    string? LoadContextName,
    int ContextId,
    int AssemblyCount,
    string[] AssemblyNames,
    DateTime LastSeenUtc,
    DateTime? LastUnloadUtc,
    DateTime? RetiredUtc,
    string? RetiredReason,
    int ForcedGcChecks,
    bool AssemblyLoadContextAlive,
    bool SuspectedRetainedAfterUnload);

public sealed record HookProbeSnapshot(
    string OwnerInternalName,
    string OwnerName,
    int OwnerGeneration,
    bool OwnerContextRetired,
    string AssemblyName,
    string HookType,
    string DelegateType,
    string BackendName,
    string Address,
    bool? IsEnabled,
    bool? IsDisposed)
{
    public Guid? RegistryId { get; init; }
}

public sealed record StaticFieldProbeSnapshot(
    string AssemblyName,
    string DeclaringType,
    string FieldName,
    string FieldType,
    string? ValueType,
    bool IsDisposable,
    bool? IsDisposed,
    bool IsNativeUiCandidate,
    int? CollectionCount,
    string? DisplayValue);

public sealed record NativeNodeProbeSnapshot(
    string TrackerName,
    string AssemblyName,
    string NodeType,
    string? Address,
    bool? IsDisposed,
    bool? IsAddonRootNode,
    bool? IsOverlayAddon,
    string? InternalName);

public sealed record PluginResourceProbeSnapshot(
    string InternalName,
    string Name,
    int Generation,
    bool IsLoaded,
    bool IsRetiredContext,
    string? LoadContextName,
    int ContextId,
    DateTime TimestampUtc,
    int HookCount,
    int EnabledHookCount,
    int UndisposedHookCount,
    HookProbeSnapshot[] Hooks,
    int StaticDisposableFieldCount,
    int UndisposedStaticDisposableFieldCount,
    int NativeUiCandidateCount,
    int KtkAllocatedNodeCount,
    int KtkCreatedNodeCount,
    int KtkCreatedAddonCount,
    int NativeNodeTrackerCount,
    int NativeAddonTrackerCount,
    NativeNodeProbeSnapshot[] NativeNodes,
    StaticFieldProbeSnapshot[] StaticFields,
    string[] Errors)
{
    public bool ReadHookRegistry { get; init; }
    public KamiRegistries ReadKamiRegistries { get; init; }
}

[Flags]
public enum KamiRegistries
{
    None = 0,
    Allocations = 1,
    Nodes = 2,
    Addons = 4,
}

public sealed record MemoryReleaseResult(
    DateTime TimestampUtc,
    MemorySnapshot Before,
    MemorySnapshot After,
    bool EmptyWorkingSetCalled,
    bool EmptyWorkingSetSucceeded,
    int LastWin32Error)
{
    public bool Collected { get; init; }
    public double ActionDurationMs { get; init; }
    public MemorySnapshot? FollowUp { get; init; }
    public bool PluginListChanged { get; init; }
}

public sealed record DiagnosticsReport(
    DateTime TimestampUtc,
    MemorySnapshot? Baseline,
    MemorySnapshot[] Snapshots,
    PluginMemoryEvent[] Events,
    PluginContextSnapshot[] PluginContexts,
    PluginResourceProbeSnapshot[] ResourceProbes,
    MemoryReleaseResult? LastReleaseResult,
    FrameCapture? ReferenceCapture,
    FrameCapture? CandidateCapture,
    string? WatchedAddon,
    AddonLifecycleEntry[] AddonEvents,
    ReloadComparison? ReloadComparison,
    AddonTiming? AddonTiming,
    ManagedProfile? ManagedProfile)
{
    public ManagedProfile? PluginScan { get; init; }
    public ReloadRun[] ReloadRuns { get; init; } = [];
}

public static class DiagnosticFormatting
{
    private static readonly string[] Units = { "B", "KiB", "MiB", "GiB", "TiB" };

    public static string FormatBytes(long bytes)
    {
        var sign = bytes < 0 ? "-" : string.Empty;
        var value = Math.Abs((double)bytes);
        var unitIndex = 0;

        while (value >= 1024.0 && unitIndex < Units.Length - 1)
        {
            value /= 1024.0;
            unitIndex++;
        }

        return unitIndex == 0
                   ? $"{sign}{value:0} {Units[unitIndex]}"
                   : $"{sign}{value:0.00} {Units[unitIndex]}";
    }

    public static string FormatSignedBytes(long bytes)
    {
        if (bytes == 0)
        {
            return "±0 B";
        }

        return bytes > 0 ? "+" + FormatBytes(bytes) : FormatBytes(bytes);
    }
}
