namespace ZeffLens.Diagnostics;

public enum ManagedProfileMode
{
    Stacks,
    Allocations,
}

public sealed record ManagedHotspot(string Module, string Method, string? Leaf, string? AllocationType, long Samples, long SampledBytes)
{
    public double EstimatedManagedMs { get; init; }
    public double EstimatedSelfMs { get; init; }
}

public sealed record PluginProfileTotal(string Plugin, long Samples, long ManagedSamples, double EstimatedManagedMs, string TopMethod);

public sealed record ManagedProfile(
    string Plugin, ManagedProfileMode Mode, double Seconds, long TotalSamples,
    long MatchedSamples, int LostEvents, ManagedHotspot[] Hotspots)
{
    public int RequestedAssemblies { get; init; }
    public int ResolvedAssemblies { get; init; }
    public int ResolvedMethods { get; init; }
    public int AmbiguousAssemblies { get; init; }
    public bool IsScan { get; init; }
    public int RequestedPlugins { get; init; }
    public int ResolvedPlugins { get; init; }
    public PluginProfileTotal[] PluginTotals { get; init; } = [];
    public ManagedHotspot[] Methods { get; init; } = [];
}

internal sealed record ProfileAssembly(string Plugin, string Identity, long ModuleId = 0);

internal sealed record ProfileRequest(int ProcessId, string Plugin, ManagedProfileMode Mode, int Seconds, string[] Assemblies,
    bool ReportProgress = false, ProfileAssembly[]? Targets = null, bool IsScan = false);
