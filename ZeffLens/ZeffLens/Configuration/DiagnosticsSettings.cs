namespace ZeffLens.Configuration;

public sealed class DiagnosticsSettings
{
    public bool AutoSampleEnabled { get; set; } = true;

    public int AutoSampleIntervalSeconds { get; set; } = 2;

    public int SettledSnapshotDelayMs { get; set; } = 1500;

    public int MaxSamples { get; set; } = 240;

    public int MaxEvents { get; set; } = 120;

    public int MaxRetiredPluginContexts { get; set; } = 200;

    public int MaxResourceProbeDetailsPerPlugin { get; set; } = 120;

    public bool CompactLargeObjectHeapOnRelease { get; set; } = true;

    public bool TrimWorkingSetOnRelease { get; set; } = true;


    public bool CaptureHookProbesOnSnapshots { get; set; }

    public bool ScanStaticFieldsForResourceProbes { get; set; }
}
