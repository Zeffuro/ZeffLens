using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using ZeffLens.Diagnostics;
using static ZeffLens.Windows.DiagnosticsWidgets;

namespace ZeffLens.Windows;

internal sealed class MemoryTab(MemoryDiagnosticsService diagnostics)
{
    private static readonly string[] MemoryMetrics = ["Private commit", "Managed heap used", "Private RAM", "Total RAM"];
    private int _memoryMetric;
    private MemoryTrend? _memoryTrend;

    public void Draw()
    {
        if (ImGui.Button("Snapshot"))
            diagnostics.CaptureSnapshot("Manual snapshot");
        ImGui.SameLine();
        if (ImGui.Button("Set baseline"))
            diagnostics.SetBaseline();
        ImGui.SameLine();
        if (ExportButton("Export full report"))
            _ = ReportExport.WriteAsync(diagnostics);

        if (diagnostics.LastExportPath is { } path)
        {
            if (ImGui.SmallButton("Copy export path"))
                ImGui.SetClipboardText(path);
            ImGui.SameLine();
            ImGui.TextDisabled(System.IO.Path.GetFileName(path));
        }

        var (latest, previous, baseline, count) = diagnostics.GetMemoryOverview();
        if (latest is null)
            return;

        var release = diagnostics.LastReleaseResult;
        var afterTrim = release is { EmptyWorkingSetSucceeded: true } && ReferenceEquals(release.After, latest);
        ImGui.TextDisabled($"{latest.Reason} at {FormatTime(latest.TimestampUtc)} ({count} samples)");
        if (afterTrim)
            ImGui.TextWrapped("This RAM reading was taken right after a trim. See the follow-up for what came back.");
        using (var table = ImRaii.Table("##Memory", 4, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV))
        {
            if (table)
            {
                ImGui.TableSetupColumn("Process memory");
                ImGui.TableSetupColumn("Current");
                ImGui.TableSetupColumn("Since last sample");
                ImGui.TableSetupColumn("Since baseline");
                ImGui.TableHeadersRow();

                DrawMemoryRow("Private commit", latest.PrivateBytes, previous?.PrivateBytes, baseline?.PrivateBytes,
                    "Process-wide allocations backed by RAM or the page file. Watch this for sustained growth.");
                DrawMemoryRow("Managed heap used", latest.ManagedLiveBytes, previous?.ManagedLiveBytes, baseline?.ManagedLiveBytes,
                    "Approximate memory occupied by .NET objects, including objects waiting to be collected.");
                DrawMemoryRow("Private RAM", latest.PrivateWorkingSetBytes, previous?.PrivateWorkingSetBytes, baseline?.PrivateWorkingSetBytes,
                    "Closest to Task Manager's Memory column. Pages can return to RAM as the game uses them.", !afterTrim);
                DrawMemoryRow("Total RAM (working set)", latest.WorkingSetBytes, previous?.WorkingSetBytes, baseline?.WorkingSetBytes,
                    "All resident pages mapped into the game, including shared pages.", !afterTrim);
                DrawMemoryRow("GC committed", latest.GcCommittedBytes, previous?.GcCommittedBytes, baseline?.GcCommittedBytes);
                DrawMemoryRow("GC fragmented", latest.GcFragmentedBytes, previous?.GcFragmentedBytes, baseline?.GcFragmentedBytes);
            }
        }

        ImGui.TextDisabled($"GC collections (0 / 1 / 2)   {latest.Gen0Collections} / {latest.Gen1Collections} / {latest.Gen2Collections}");
        ImGui.TextDisabled($"Last observed plugin contexts   {latest.LoadedPluginContextCount}");

        DrawMemoryTrend(latest, baseline);
        DrawMemoryTools(release);
    }

    private void DrawMemoryTools(MemoryReleaseResult? release)
    {
        if (!ImGui.CollapsingHeader("Memory tools"))
            return;

        var busy = !diagnostics.CanRunMemoryAction;
        using (ImRaii.Disabled(busy))
        {
            if (ImGui.Button("Trim working set"))
                diagnostics.ReleaseWorkingMemory(collect: false);
            ImGui.SameLine();
            if (ImGui.Button("Run full GC"))
                ImGui.OpenPopup("Run full GC?");
        }

        if (DrawCollectionConfirmation(busy))
            diagnostics.ReleaseWorkingMemory(trim: false);

        if (release is not null)
            DrawMemoryActionResult(release);
        else
            ImGui.TextWrapped("Trim evicts resident pages. Full GC collects unused .NET objects. Each action takes a follow-up sample after five seconds.");
    }

    private static bool DrawCollectionConfirmation(bool busy)
    {
        const string title = "Run full GC?";
        if (!ImGui.IsPopupOpen(title))
            return false;

        var viewport = ImGui.GetMainViewport();
        ImGui.SetNextWindowPos(viewport.WorkPos + viewport.WorkSize / 2, ImGuiCond.Appearing, new Vector2(0.5f));
        ImGui.SetNextWindowSize(new Vector2(Math.Min(viewport.WorkSize.X, ImGui.GetFontSize() * 29), 0), ImGuiCond.Always);
        using var popup = ImRaii.PopupModal(title, ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoResize);
        if (!popup)
            return false;

        ImGui.TextWrapped("Full .NET garbage collection can pause the game. ZeffLens will sample memory before, immediately after, and again five seconds later.");
        bool confirmed;
        using (ImRaii.Disabled(busy))
        {
            confirmed = ImGui.Button("Run full GC");
        }
        ImGui.SameLine();
        var cancelled = ImGui.Button("Cancel");
        ImGui.SetItemDefaultFocus();
        if (confirmed || cancelled)
            ImGui.CloseCurrentPopup();
        return confirmed;
    }

    private static void DrawMemoryActionResult(MemoryReleaseResult result)
    {
        var action = result.Collected ? result.EmptyWorkingSetCalled ? "Full GC and trim" : "Full GC" : "Working-set trim";
        ImGui.TextUnformatted($"{action} at {FormatTime(result.TimestampUtc)} ({result.ActionDurationMs:0.#} ms)");
        if (result.FollowUp is null)
            ImGui.TextDisabled("Waiting for the follow-up sample…");
        if (result.PluginListChanged)
            ImGui.TextColored(WarningColor, "Plugin list changed before the follow-up.");
        if (result.EmptyWorkingSetCalled && !result.EmptyWorkingSetSucceeded)
            ImGui.TextColored(WarningColor, $"Working-set trim failed (Win32 {result.LastWin32Error})");

        using (var table = ImRaii.Table("##MemoryAction", 4, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV))
        {
            if (table)
            {
                ImGui.TableSetupColumn("Process memory");
                ImGui.TableSetupColumn("Before");
                ImGui.TableSetupColumn("Immediately after");
                ImGui.TableSetupColumn(result.FollowUp is { } sample
                    ? $"Follow-up ({(sample.TimestampUtc - result.After.TimestampUtc).TotalSeconds:0.#}s later)"
                    : "Follow-up");
                ImGui.TableHeadersRow();
                Row("Private commit", static point => point.PrivateBytes);
                Row("Managed heap used", static point => point.ManagedLiveBytes);
                Row("Private RAM", static point => point.PrivateWorkingSetBytes);
                Row("Total RAM (working set)", static point => point.WorkingSetBytes);
            }
        }

        if (result.EmptyWorkingSetSucceeded)
            ImGui.TextWrapped("Pages can return to RAM as the game uses them. Compare private commit and managed heap to see what the process still holds.");

        void Row(string label, Func<MemorySnapshot, long?> read)
        {
            var before = read(result.Before);
            ImGui.TableNextRow();
            DrawTableCell(label);
            DrawTableCell(FormatMemoryValue(before));
            DrawTableCell(FormatMemoryValue(read(result.After), before));
            DrawTableCell(FormatMemoryValue(result.FollowUp is { } followUp ? read(followUp) : null, before));
        }
    }

    private static string FormatMemoryValue(long? value, long? before = null)
        => value is not { } bytes ? "—" : before is { } baseline
            ? $"{DiagnosticFormatting.FormatBytes(bytes)} ({DiagnosticFormatting.FormatSignedBytes(bytes - baseline)})"
            : DiagnosticFormatting.FormatBytes(bytes);

    private static void DrawMemoryRow(string label, long? value, long? previousValue, long? baselineValue,
        string? help = null, bool colorDeltas = true)
    {
        ImGui.TableNextRow();
        ImGui.TableNextColumn();
        ImGui.TextUnformatted(label);
        if (help is not null && ImGui.IsItemHovered())
            ImGui.SetTooltip(help);
        ImGui.TableNextColumn();
        ImGui.TextUnformatted(value is { } bytes ? DiagnosticFormatting.FormatBytes(bytes) : "—");
        ImGui.TableNextColumn();
        DrawDeltaOnly(value, previousValue, colorDeltas);
        ImGui.TableNextColumn();
        DrawDeltaOnly(value, baselineValue, colorDeltas);
    }

    private void DrawMemoryTrend(MemorySnapshot latest, MemorySnapshot? baseline)
    {
        ImGui.Separator();
        ImGui.SetNextItemWidth(ImGui.GetFontSize() * 15);
        ImGui.Combo("Trend", ref _memoryMetric, MemoryMetrics, MemoryMetrics.Length);
        ImGui.SameLine();
        if (_memoryTrend is not { } trend || !ReferenceEquals(trend.Latest, latest) || trend.Metric != _memoryMetric)
            _memoryTrend = trend = BuildMemoryTrend(latest, _memoryMetric);

        var points = trend.Points;
        if (points.Length < 2)
        {
            ImGui.TextDisabled(points.Length == 0 ? "No readings for this metric." : "Waiting for another sample.");
            return;
        }

        var first = points[0];
        var last = points[^1];
        ImGui.TextUnformatted($"{DiagnosticFormatting.FormatBytes(last.Bytes)} ({DiagnosticFormatting.FormatSignedBytes(last.Bytes - first.Bytes)} in view)");
        ImGui.TextDisabled($"{FormatTime(first.Sample.TimestampUtc)} to {FormatTime(last.Sample.TimestampUtc)} ({points.Length} samples)");

        var plotSize = new Vector2(ImGui.GetContentRegionAvail().X, ImGui.GetFontSize() * 6);
        if (plotSize.X < 40)
            return;
        ImGui.InvisibleButton("##MemoryTrend", plotSize);
        if (!ImGui.IsItemVisible())
            return;

        var min = ImGui.GetItemRectMin();
        var max = ImGui.GetItemRectMax();
        var draw = ImGui.GetWindowDrawList();
        draw.AddRectFilled(min, max, ImGui.GetColorU32(ImGuiCol.FrameBg));
        var start = min + new Vector2(6);
        var end = max - new Vector2(6);
        var seconds = (last.Sample.TimestampUtc - first.Sample.TimestampUtc).TotalSeconds;
        var mouse = ImGui.GetMousePos().X;
        var closest = 0;
        var distance = float.PositiveInfinity;
        Vector2 cursor = default;
        Vector2 previous = default;
        var line = ImGui.GetColorU32(ImGuiCol.PlotLines);
        for (var i = 0; i < points.Length; i++)
        {
            var elapsed = (points[i].Sample.TimestampUtc - first.Sample.TimestampUtc).TotalSeconds;
            var fraction = seconds > 0 ? (float)Math.Clamp(elapsed / seconds, 0, 1) : i / (points.Length - 1f);
            var position = new Vector2(start.X + fraction * (end.X - start.X), Y(points[i].Bytes));
            if (i > 0)
                draw.AddLine(previous, position, line, 1.5f);
            previous = position;
            var dx = Math.Abs(position.X - mouse);
            if (dx <= distance)
            {
                closest = i;
                distance = dx;
                cursor = position;
            }
        }

        if (ImGui.IsItemHovered())
        {
            var point = points[closest];
            draw.AddLine(new Vector2(cursor.X, start.Y), new Vector2(cursor.X, end.Y), ImGui.GetColorU32(ImGuiCol.PlotLinesHovered));
            var baselineBytes = baseline is null ? null : ReadMemoryMetric(baseline, _memoryMetric);
            ImGui.SetTooltip($"{FormatTime(point.Sample.TimestampUtc)}  {point.Sample.Reason}\n" +
                FormatMemoryValue(point.Bytes, baselineBytes) + (baselineBytes.HasValue ? " vs. baseline" : string.Empty));
        }
        ImGui.TextDisabled($"Scale shown {DiagnosticFormatting.FormatBytes(trend.Minimum)} to {DiagnosticFormatting.FormatBytes(trend.Maximum)}");

        float Y(long bytes) => end.Y - (float)((bytes - trend.Minimum) / (double)(trend.Maximum - trend.Minimum)) * (end.Y - start.Y);
    }

    private MemoryTrend BuildMemoryTrend(MemorySnapshot latest, int metric)
    {
        var samples = diagnostics.GetRecentSamples(120);
        var points = new List<TrendPoint>(samples.Length);
        var low = long.MaxValue;
        var high = 0L;
        foreach (var sample in samples)
        {
            if (ReadMemoryMetric(sample, metric) is not { } bytes)
                continue;
            low = Math.Min(low, bytes);
            high = Math.Max(high, bytes);
            points.Add(new TrendPoint(sample, bytes));
        }

        var padding = points.Count > 0 ? Math.Max(8 * 1024 * 1024L, (high - low) / 10) : 0;
        return new MemoryTrend(latest, metric, points.ToArray(), Math.Max(0, low - padding), high + padding);
    }

    private static long? ReadMemoryMetric(MemorySnapshot sample, int metric) => metric switch
    {
        1 => sample.ManagedLiveBytes,
        2 => sample.PrivateWorkingSetBytes,
        3 => sample.WorkingSetBytes,
        _ => sample.PrivateBytes,
    };

    private sealed record MemoryTrend(MemorySnapshot Latest, int Metric, TrendPoint[] Points, long Minimum, long Maximum);
    private readonly record struct TrendPoint(MemorySnapshot Sample, long Bytes);
}
