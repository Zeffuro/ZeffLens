using System;
using System.Text;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using ZeffLens.Diagnostics;
using static ZeffLens.Windows.DiagnosticsWidgets;

namespace ZeffLens.Windows;

internal sealed class PluginProfilerTab(MemoryDiagnosticsService diagnostics)
{
    private string? _profilePlugin;
    private string _profileSearch = string.Empty;
    private int _profileSeconds = 10;
    private ManagedProfileMode _profileMode;
    private bool _showProfileDetails;
    private ManagedHotspot[]? _displayedHotspots;
    private string[] _shortMethods = [];
    private string[] _shortDetails = [];

    private ManagedProfile? _displayedScan;
    private string[] _scanMethods = [];

    public bool FocusRequested { get; set; }

    public void Draw()
    {
        var (progress, cancelling, result, _, error) = diagnostics.ManagedProfiles.Read();
        if (DrawManagedCaptureProgress(progress, cancelling))
            return;

        DrawLoadedPluginPicker(diagnostics, "Profile target", ref _profilePlugin, ref _profileSearch, includeZeffLens: true);
        ImGui.SetNextItemWidth(140);
        using (var combo = ImRaii.Combo("Mode", ProfileModeLabel(_profileMode)))
        {
            if (combo)
            {
                if (ImGui.Selectable("Managed stacks", _profileMode == ManagedProfileMode.Stacks))
                    _profileMode = ManagedProfileMode.Stacks;

                if (ImGui.Selectable("Allocations", _profileMode == ManagedProfileMode.Allocations))
                    _profileMode = ManagedProfileMode.Allocations;
            }
        }

        ImGui.SameLine();
        ImGui.SetNextItemWidth(100);
        if (ImGui.InputInt("Seconds", ref _profileSeconds, 5))
            _profileSeconds = Math.Clamp(_profileSeconds, 5, 20);
        ImGui.SameLine();
        var recording = diagnostics.Frames.IsRecording || diagnostics.AddonProfiles.IsRunning;
        var reloading = diagnostics.ReloadInProgress;
        using (ImRaii.Disabled(recording || reloading || _profilePlugin is null))
        {
            if (ImGui.Button("Start profile") && _profilePlugin is { } selected)
                diagnostics.ManagedProfiles.Start(selected, _profileMode, _profileSeconds);
        }

        if (reloading)
            ImGui.TextDisabled("Wait for the plugin to reload.");
        else if (recording)
            ImGui.TextDisabled("Another recording is running.");
        else
            ImGui.TextDisabled("Repeat the slow action while recording.");

        if (error is not null)
            ImGui.TextColored(WarningColor, error);

        if (result is null)
            return;

        ImGui.Separator();
        ImGui.TextUnformatted($"{result.Plugin}: {ProfileModeLabel(result.Mode)} ({result.Seconds:0.0}s)");
        ImGui.SameLine();
        if (ExportButton("Export profile JSON"))
            _ = ReportExport.WriteAsync(diagnostics, result);

        ImGui.TextDisabled($"{result.MatchedSamples:N0} of {result.TotalSamples:N0} samples matched");
        if (result.LostEvents > 0)
            ImGui.TextColored(WarningColor, $"Trace incomplete: .NET dropped {result.LostEvents:N0} events.");
        if (result.Hotspots.Length == 0)
        {
            var message = result switch
            {
                { AmbiguousAssemblies: > 0 } => "Multiple loads share an assembly name.",
                { ResolvedMethods: 0 } => "The trace could not resolve the plugin’s methods.",
                { LostEvents: > 0 } => "No usable samples for this plugin.",
                _ => $"No matches for {result.Plugin}. Repeat the action while recording.",
            };
            ImGui.TextWrapped(message);
            return;
        }

        ImGui.Checkbox(result.Mode == ManagedProfileMode.Stacks ? "Break down by stack leaf" : "Break down by allocated type", ref _showProfileDetails);
        ImGui.SameLine();
        var detailed = _showProfileDetails || result.Methods.Length == 0;
        var rows = detailed ? result.Hotspots : result.Methods;
        if (ImGui.Button("Copy hotspots"))
            CopyManagedHotspots(result, rows, detailed);

        ImGui.TextDisabled(result.Mode == ManagedProfileMode.Allocations
            ? "Sampled allocation sizes. Right-click to copy."
            : "Sampled managed time across threads. Right-click to copy.");
        DrawManagedHotspots(result, rows, detailed);
    }

    private bool DrawManagedCaptureProgress(ManagedProfiler.ProfileProgress? progress, bool cancelling)
    {
        if (progress is not { } active)
            return false;

        var status = cancelling ? "Cancelling…" : active.Finishing ? "Reading the trace…" :
            active.SecondsLeft is { } remaining ? $"Recording {active.Plugin} ({Math.Ceiling(remaining):0}s left)" :
            $"Starting profiler for {active.Plugin}…";
        ImGui.TextUnformatted(status);
        ImGui.SameLine();
        using (ImRaii.Disabled(cancelling))
        {
            if (ImGui.Button("Cancel"))
                diagnostics.ManagedProfiles.Cancel();
        }
        return true;
    }

    private void DrawManagedHotspots(ManagedProfile result, ManagedHotspot[] rows, bool detailed)
    {
        var allocation = result.Mode == ManagedProfileMode.Allocations;
        if (!ReferenceEquals(rows, _displayedHotspots))
        {
            _displayedHotspots = rows;
            _shortMethods = Array.ConvertAll(rows, row => ShortMethod(row.Method));
            _shortDetails = Array.ConvertAll(rows, row => allocation
                ? row.AllocationType ?? "—"
                : row.Leaf is { } leaf ? ShortMethod(leaf) : "—");
        }

        using var table = ImRaii.Table("##ManagedHotspots", 3 + (detailed ? 1 : 0) + (allocation ? 0 : 1), ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV);
        if (!table)
            return;

        ImGui.TableSetupColumn("Plugin method", ImGuiTableColumnFlags.WidthStretch, 2);
        if (detailed)
            ImGui.TableSetupColumn(allocation ? "Allocated type" : "Stack leaf");
        ImGui.TableSetupColumn("Samples", ImGuiTableColumnFlags.WidthFixed, 75);
        ImGui.TableSetupColumn(allocation ? "Est. bytes" : "Est. ms", ImGuiTableColumnFlags.WidthFixed, 100);
        if (!allocation)
            ImGui.TableSetupColumn("Est. self ms", ImGuiTableColumnFlags.WidthFixed, 100);
        ImGui.TableHeadersRow();

        for (var i = 0; i < rows.Length; i++)
        {
            var hotspot = rows[i];
            using var id = ImRaii.PushId(i);
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            DrawProfileCell(_shortMethods[i], hotspot.Method, "Copy method", hotspot, allocation, detailed);
            if (detailed)
            {
                ImGui.TableNextColumn();
                DrawProfileCell(_shortDetails[i], allocation ? hotspot.AllocationType : hotspot.Leaf,
                    allocation ? "Copy type" : "Copy stack leaf", hotspot, allocation, detailed);
            }
            DrawTableCell(hotspot.Samples.ToString("N0"));
            DrawTableCell(allocation ? DiagnosticFormatting.FormatBytes(hotspot.SampledBytes) :
                FormatProfileTime(hotspot.EstimatedManagedMs));
            if (!allocation)
                DrawTableCell(FormatProfileTime(hotspot.EstimatedSelfMs));
        }
    }

    private static void DrawProfileCell(string label, string? fullName, string copyLabel, ManagedHotspot row, bool allocation, bool detailed)
    {
        ImGui.TextWrapped(label);
        if (fullName is not null && ImGui.IsItemHovered())
            ImGui.SetTooltip(fullName);
        using var popup = ImRaii.ContextPopupItem(copyLabel);
        if (!popup)
            return;

        if (fullName is not null && ImGui.MenuItem(copyLabel))
            ImGui.SetClipboardText(fullName);
        if (ImGui.MenuItem("Copy row"))
            ImGui.SetClipboardText(FormatHotspotRow(row, allocation, detailed));
    }

    private static void CopyManagedHotspots(ManagedProfile result, ManagedHotspot[] rows, bool detailed)
    {
        var allocation = result.Mode == ManagedProfileMode.Allocations;
        var text = new StringBuilder().AppendLine($"{result.Plugin} ({ProfileModeLabel(result.Mode)}, {result.Seconds:0.0}s)")
            .AppendLine($"{result.MatchedSamples:N0} of {result.TotalSamples:N0} samples matched, {result.LostEvents:N0} events lost")
            .Append("Module\tPlugin method");
        if (detailed)
            text.Append(allocation ? "\tAllocated type" : "\tStack leaf");
        text.AppendLine(allocation ? "\tSamples\tEstimated bytes" : "\tSamples\tEstimated ms\tEstimated self ms");
        foreach (var row in rows)
            text.AppendLine(FormatHotspotRow(row, allocation, detailed));
        ImGui.SetClipboardText(text.ToString());
    }

    private static string FormatHotspotRow(ManagedHotspot row, bool allocation, bool detailed)
    {
        var detail = detailed ? $"\t{(allocation ? row.AllocationType : row.Leaf)}" : string.Empty;
        return allocation
            ? FormattableString.Invariant($"{row.Module}\t{row.Method}{detail}\t{row.Samples}\t{row.SampledBytes}")
            : FormattableString.Invariant($"{row.Module}\t{row.Method}{detail}\t{row.Samples}\t{row.EstimatedManagedMs:0.00}\t{row.EstimatedSelfMs:0.00}");
    }

    private static string ShortMethod(string method)
    {
        var parameters = method.IndexOf('(');
        var end = parameters < 0 ? method.Length : parameters;
        var member = method.AsSpan(0, end);
        var memberSeparator = member.LastIndexOf('.');
        if (memberSeparator > 0)
            member = member[(member[..memberSeparator].LastIndexOf('.') + 1)..];
        var suffix = parameters < 0 ? string.Empty : method.AsSpan(parameters).SequenceEqual("()") ? "()" : "(…)";
        return string.Concat(member, suffix);
    }

    private static string FormatProfileTime(double ms) => ms > 0 ? $"{ms:0.0}" : "—";
    private static string ProfileModeLabel(ManagedProfileMode mode) => mode == ManagedProfileMode.Stacks ? "Managed stacks" : "Allocations";

    public void DrawScan()
    {
        var (progress, cancelling, _, scan, error) = diagnostics.ManagedProfiles.Read();
        if (DrawManagedCaptureProgress(progress, cancelling))
            return;

        ImGui.TextDisabled("Repeat the slow action during the scan. Select a result to profile it.");
        ImGui.SetNextItemWidth(100);
        if (ImGui.InputInt("Seconds", ref _profileSeconds, 5))
            _profileSeconds = Math.Clamp(_profileSeconds, 5, 20);
        ImGui.SameLine();
        using (ImRaii.Disabled(diagnostics.Frames.IsRecording || diagnostics.AddonProfiles.IsRunning || diagnostics.ReloadInProgress))
        {
            if (ImGui.Button("Scan loaded plugins"))
                diagnostics.ManagedProfiles.Start(null, ManagedProfileMode.Stacks, _profileSeconds);
        }
        if (ImGui.Button("Open Dalamud callback timings"))
            Service.CommandManager.ProcessCommand("/xlstats");
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Framework and ImGui callbacks. Sort by Longest for spikes or Average for steady work.");

        if (error is not null)
            ImGui.TextColored(WarningColor, error);
        if (scan is null)
            return;

        ImGui.Separator();
        ImGui.TextUnformatted($"{scan.Seconds:0.0}s scan, {scan.ResolvedPlugins:N0} of {scan.RequestedPlugins:N0} plugins resolved");
        ImGui.SameLine();
        if (ExportButton("Export scan JSON"))
            _ = ReportExport.WriteAsync(diagnostics, scan);
        if (scan.LostEvents > 0)
            ImGui.TextColored(WarningColor, $"Trace incomplete: .NET dropped {scan.LostEvents:N0} events.");
        if (scan.PluginTotals.Length == 0)
        {
            ImGui.TextWrapped("No loaded plugin methods matched. Try repeating the slow action during the countdown.");
            return;
        }

        DrawPluginScanTable(scan);
    }

    private void DrawPluginScanTable(ManagedProfile scan)
    {
        if (!ReferenceEquals(scan, _displayedScan))
        {
            _displayedScan = scan;
            _scanMethods = Array.ConvertAll(scan.PluginTotals, row => ShortMethod(row.TopMethod));
        }

        using var table = ImRaii.Table("##RankedPlugins", 4, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV);
        if (!table)
            return;

        ImGui.TableSetupColumn("Plugin");
        ImGui.TableSetupColumn("Est. managed ms", ImGuiTableColumnFlags.WidthFixed, 125);
        ImGui.TableSetupColumn("Managed hits", ImGuiTableColumnFlags.WidthFixed, 100);
        ImGui.TableSetupColumn("Top method");
        ImGui.TableHeadersRow();

        for (var i = 0; i < scan.PluginTotals.Length; i++)
        {
            var row = scan.PluginTotals[i];
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            if (ImGui.Selectable(row.Plugin))
            {
                _profilePlugin = row.Plugin;
                _profileMode = ManagedProfileMode.Stacks;
                FocusRequested = true;
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Select plugin for a closer look");
            DrawTableCell(FormatProfileTime(row.EstimatedManagedMs));
            DrawTableCell(row.ManagedSamples.ToString("N0"));
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip($"{row.Samples:N0} total stack matches including external frames");
            DrawTableCell(_scanMethods[i]);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(row.TopMethod);
        }
    }
}
