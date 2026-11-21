using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using ZeffLens.Diagnostics;

namespace ZeffLens;

public sealed partial class MemoryDiagnosticsService
{
    public string ExportReport()
    {
        ThrowIfDisposed();

        Frames.NoteInterference();
        var (reference, candidate, _) = Frames.Read();
        var (watchedAddon, addonEvents) = Addons.Read();
        var addonTiming = AddonProfiles.Read().Last;
        var (_, _, managedProfile, pluginScan, _) = ManagedProfiles.Read();

        DiagnosticsReport report;
        lock (_gate)
        {
            ThrowIfDisposed();
            report = new DiagnosticsReport(
                DateTime.UtcNow,
                Baseline,
                _snapshots.ToArray(),
                GetEvents(),
                GetPluginContexts(),
                _resourceProbes,
                LastReleaseResult,
                reference,
                candidate,
                watchedAddon,
                addonEvents,
                _reloadComparison,
                addonTiming,
                managedProfile)
            {
                PluginScan = pluginScan,
                ReloadRuns = GetReloadRuns(),
            };
        }

        return WriteReport("ZeffLens", report);
    }

    internal string ExportReport(ManagedProfile profile)
        => ExportCapture(profile.IsScan ? "plugin-scan" : "plugin-profile", profile);

    internal string ExportReport(AddonTiming timing)
        => ExportCapture("addon-timing", timing);

    internal string ExportReport(FrameCapture? reference, FrameCapture? candidate)
        => ExportCapture("frame-pacing", new { ReferenceCapture = reference, CandidateCapture = candidate });

    internal string ExportReport(ReloadComparison comparison)
        => ExportCapture("reload-comparison", comparison);

    internal string ExportReport(ReloadRun[] runs)
        => ExportCapture("reload-trends", new { ReloadRuns = runs });

    private string ExportCapture<T>(string reportName, T report)
    {
        ThrowIfDisposed();
        Frames.NoteInterference();
        return WriteReport($"ZeffLens-{reportName}", report);
    }

    private string WriteReport<T>(string filePrefix, T report)
    {
        var reportsDirectory = Path.Combine(Service.PluginInterface.GetPluginConfigDirectory(), "reports");
        Directory.CreateDirectory(reportsDirectory);

        var path = Path.Combine(reportsDirectory, $"{filePrefix}-{DateTime.Now:yyyyMMdd-HHmmss-fff}.json");
        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
        };
        options.Converters.Add(new JsonStringEnumConverter());

        var temporary = Path.Combine(reportsDirectory, $".{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                JsonSerializer.Serialize(stream, report, options);
            File.Move(temporary, path);
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }

        lock (_gate)
        {
            LastExportPath = path;
        }

        return path;
    }
}
