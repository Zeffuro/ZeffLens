using System;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using ZeffLens.Configuration.Persistence;
using static ZeffLens.Windows.DiagnosticsWidgets;

namespace ZeffLens.Windows;

internal sealed class SettingsTab(MemoryDiagnosticsService diagnostics)
{
    public void Draw()
    {
        var settings = PluginState.Config.Diagnostics;

        var autoSample = settings.AutoSampleEnabled;
        if (ImGui.Checkbox("Auto sample", ref autoSample))
        {
            settings.AutoSampleEnabled = autoSample;
            SaveConfig();
        }

        var interval = settings.AutoSampleIntervalSeconds;
        if (ImGui.InputInt("Sample interval (seconds)", ref interval))
        {
            settings.AutoSampleIntervalSeconds = Math.Clamp(interval, 1, 60);
            SaveConfig();
        }

        var settleDelay = settings.SettledSnapshotDelayMs;
        if (ImGui.InputInt("Post-reload sample delay (ms)", ref settleDelay))
        {
            settings.SettledSnapshotDelayMs = Math.Clamp(settleDelay, 0, 30000);
            SaveConfig();
        }

        var maxSamples = settings.MaxSamples;
        if (ImGui.InputInt("Max samples", ref maxSamples))
        {
            settings.MaxSamples = Math.Clamp(maxSamples, 20, 2000);
            SaveConfig();
        }

        var maxEvents = settings.MaxEvents;
        if (ImGui.InputInt("Max plugin events", ref maxEvents))
        {
            settings.MaxEvents = Math.Clamp(maxEvents, 20, 1000);
            SaveConfig();
        }

        var maxRetiredContexts = settings.MaxRetiredPluginContexts;
        if (ImGui.InputInt("Max retired plugin contexts", ref maxRetiredContexts))
        {
            settings.MaxRetiredPluginContexts = Math.Clamp(maxRetiredContexts, 20, 2000);
            SaveConfig();
        }

        var maxProbeDetails = settings.MaxResourceProbeDetailsPerPlugin;
        if (ImGui.InputInt("Max probe details per plugin", ref maxProbeDetails))
        {
            settings.MaxResourceProbeDetailsPerPlugin = Math.Clamp(maxProbeDetails, 20, 1000);
            SaveConfig();
        }

        var compactLoh = settings.CompactLargeObjectHeapOnRelease;
        if (ImGui.Checkbox("Compact large object heap during GC", ref compactLoh))
        {
            settings.CompactLargeObjectHeapOnRelease = compactLoh;
            SaveConfig();
        }

        var trimWorkingSet = settings.TrimWorkingSetOnRelease;
        if (ImGui.Checkbox("Trim after /zefflens gc or release", ref trimWorkingSet))
        {
            settings.TrimWorkingSetOnRelease = trimWorkingSet;
            SaveConfig();
        }

        var captureHookProbes = settings.CaptureHookProbesOnSnapshots;
        if (ImGui.Checkbox("Probe resources on manual snapshots", ref captureHookProbes))
        {
            settings.CaptureHookProbesOnSnapshots = captureHookProbes;
            SaveConfig();
        }

        var scanStaticFields = settings.ScanStaticFieldsForResourceProbes;
        if (ImGui.Checkbox("List static field types in retired contexts", ref scanStaticFields))
        {
            settings.ScanStaticFieldsForResourceProbes = scanStaticFields;
            SaveConfig();
        }

        ImGui.Separator();
        if (ImGui.Button("Clear history"))
            ImGui.OpenPopup("Clear history?");
        using (var popup = ImRaii.PopupModal("Clear history?"))
        {
            if (popup)
            {
                ImGui.TextUnformatted("Clear memory, reload, performance and addon history.");
                if (ImGui.Button("Clear"))
                {
                    diagnostics.ClearHistory();
                    ImGui.CloseCurrentPopup();
                }
                ImGui.SameLine();
                if (ImGui.Button("Cancel"))
                    ImGui.CloseCurrentPopup();
            }
        }
    }

    private static void SaveConfig()
    {
        ConfigRepository.Save(PluginState.Config);
    }
}
