using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using ZeffLens.Diagnostics;
using static ZeffLens.Windows.DiagnosticsWidgets;

namespace ZeffLens.Windows;

internal sealed class FramePacingTab(MemoryDiagnosticsService diagnostics)
{
    private const int PlotBuckets = 120;
    private int _captureSeconds = 15;
    private string _referenceLabel = "";
    private string _candidateLabel = "";
    private CapturePlot? _referencePlot;
    private CapturePlot? _candidatePlot;
    private bool _captureGraphicsMemory;

    public void Draw()
    {
        var (reference, candidate, progress) = diagnostics.Frames.Read();
        if (progress is { } elapsed)
        {
            ImGui.TextUnformatted(elapsed < 0 ? $"Starting in {-elapsed:0.0}s. Get ready to repeat the action." : $"Recording ({elapsed:0.0}s). Do the action now.");
            ImGui.SameLine();
            if (ImGui.Button("Cancel"))
                diagnostics.Frames.Cancel();
            if (elapsed >= 0)
            {
                ImGui.SameLine();
                if (ImGui.Button("Mark"))
                    diagnostics.Frames.Mark(string.Empty);
            }
            return;
        }

        ImGui.TextWrapped("Compare overall stutter before and after a plugin or setting change.");
        ImGui.SetNextItemWidth(100);
        if (ImGui.InputInt("Seconds per run", ref _captureSeconds, 5))
            _captureSeconds = Math.Clamp(_captureSeconds, 5, 60);
        ImGui.Checkbox("Sample graphics memory", ref _captureGraphicsMemory);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Read video memory usage and budget at the start and end of the run.");

        using (ImRaii.Disabled(diagnostics.ManagedProfiles.IsRunning || diagnostics.AddonProfiles.IsRunning || diagnostics.ReloadInProgress))
        {
            DrawCaptureControl(true, ref _referenceLabel, "Before", reference is not null);
            using (ImRaii.Disabled(reference is null))
            {
                DrawCaptureControl(false, ref _candidateLabel, "After", candidate is not null);
            }
        }
        ImGui.TextWrapped(reference is null
            ? "Record Before, then do the action when the 3-second countdown ends."
            : candidate is null ? "Change one plugin or setting, then record After and repeat the same action."
            : "Compare p95 and the slowest updates below. Lower values mean shorter waits between updates.");
        if (ImGui.Button("Open Dalamud callback timings"))
            Service.CommandManager.ProcessCommand("/xlstats");
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("See individual plugin callbacks in Dalamud's stats window.");

        if (reference is null && candidate is null)
            return;

        ImGui.SameLine();
        if (ExportButton("Export frame JSON"))
            _ = ReportExport.WriteAsync(diagnostics, reference, candidate);

        ImGui.Separator();
        ImGui.TextUnformatted("Framework update intervals");
        ImGui.TextDisabled("These timings and GC counters cover the whole game.");
        DrawCaptureSummary(reference, candidate);
        DrawGraphicsMemoryComparison(reference?.GraphicsMemory, candidate?.GraphicsMemory);

        if (reference is not null && candidate is not null && reference.TargetSeconds != candidate.TargetSeconds)
            ImGui.TextWrapped("Run lengths differ. Compare percentile times and per-second rates.");
        if (reference?.Interrupted == true || candidate?.Interrupted == true)
            ImGui.TextColored(WarningColor, "A plugin changed or a ZeffLens diagnostic ran during capture.");
        if (reference?.SampleLimitReached == true || candidate?.SampleLimitReached == true)
            ImGui.TextColored(WarningColor, "A run ended at the sample limit.");

        var maxTime = Math.Max(reference?.MaxMs ?? 0, candidate?.MaxMs ?? 0);
        var maxDuration = Math.Max(reference?.DurationSeconds ?? 0, candidate?.DurationSeconds ?? 0);
        UpdateCapturePlot(ref _referencePlot, reference, maxDuration);
        UpdateCapturePlot(ref _candidatePlot, candidate, maxDuration);
        ImGui.TextWrapped("Click an update bar for nearby events. Top ticks mark actions and addons. Bottom ticks mark GC changes.");

        if (_referencePlot is { } referencePlot)
            DrawCapturePlot("Before", referencePlot, maxTime);
        if (_candidatePlot is { } candidatePlot)
            DrawCapturePlot("After", candidatePlot, maxTime);
    }

    private void DrawCaptureControl(bool reference, ref string label, string slot, bool alreadyRecorded)
    {
        using var id = ImRaii.PushId(slot);
        ImGui.SetNextItemWidth(190);
        if (ImGui.Button(alreadyRecorded ? $"Redo {slot}###Record" : $"Record {slot}###Record"))
            diagnostics.Frames.Start(reference, string.IsNullOrWhiteSpace(label) ? slot : label, _captureSeconds, _captureGraphicsMemory);
        ImGui.SameLine();
        ImGui.InputTextWithHint("##Label", $"{slot} label (optional)", ref label, 80);
    }

    private static void DrawCaptureSummary(FrameCapture? reference, FrameCapture? candidate)
    {
        using var table = ImRaii.Table("##CaptureSummary", 4, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV);
        if (!table)
            return;

        ImGui.TableSetupColumn("Metric");
        ImGui.TableSetupColumn("Before");
        ImGui.TableSetupColumn("After");
        ImGui.TableSetupColumn("After − before");
        ImGui.TableHeadersRow();

        DrawCaptureRow("Run", reference?.Label, candidate?.Label);
        DrawCaptureRow("Captured", reference is null ? null : $"{reference.DurationSeconds:0.0}s", candidate is null ? null : $"{candidate.DurationSeconds:0.0}s");
        DrawTiming("Median", reference?.MedianMs, candidate?.MedianMs);
        DrawTiming("p95 (95% of updates at or under)", reference?.P95Ms, candidate?.P95Ms);
        DrawTiming("99th percentile", reference?.P99Ms, candidate?.P99Ms);
        DrawTiming("Slowest", reference?.MaxMs, candidate?.MaxMs);
        DrawCaptureRow("Updates > 33.3 ms/s", Rate(reference, reference?.Over33Ms), Rate(candidate, candidate?.Over33Ms));
        DrawCaptureRow("Updates > 50 ms/s", Rate(reference, reference?.Over50Ms), Rate(candidate, candidate?.Over50Ms));
        DrawCaptureRow("Managed alloc/s", AllocationRate(reference), AllocationRate(candidate));
        DrawCaptureRow("GC (gen 0 / 1 / 2)", Collections(reference), Collections(candidate));

        static string? Rate(FrameCapture? capture, int? count)
            => capture is null ? null : $"{count / capture.DurationSeconds:0.00}";
        static string? AllocationRate(FrameCapture? capture)
            => capture is null ? null : DiagnosticFormatting.FormatBytes((long)(capture.AllocatedBytes / capture.DurationSeconds));
        static string? Collections(FrameCapture? capture)
            => capture is null ? null : $"{capture.Gen0Collections} / {capture.Gen1Collections} / {capture.Gen2Collections}";
    }

    private static void DrawTiming(string label, float? reference, float? candidate)
    {
        var delta = candidate - reference;
        DrawCaptureRow(label, reference is null ? null : $"{reference:0.00} ms",
                       candidate is null ? null : $"{candidate:0.00} ms",
                       delta is null ? null : $"{delta:+0.00;-0.00;0.00} ms");
    }

    private static void DrawCaptureRow(string label, string? reference, string? candidate, string? change = null)
    {
        ImGui.TableNextRow();
        ImGui.TableNextColumn();
        ImGui.TextUnformatted(label);
        ImGui.TableNextColumn();
        ImGui.TextUnformatted(reference ?? "—");
        ImGui.TableNextColumn();
        ImGui.TextUnformatted(candidate ?? "—");
        ImGui.TableNextColumn();
        ImGui.TextUnformatted(change ?? "—");
    }

    private static void UpdateCapturePlot(ref CapturePlot? plot, FrameCapture? capture, double duration)
    {
        if (capture is null)
            plot = null;
        else if (!ReferenceEquals(plot?.Timeline.Capture, capture) || plot.Timeline.Duration != duration)
            plot = new CapturePlot(new FrameTimeline(capture, duration, PlotBuckets));
    }

    private static void DrawCapturePlot(string slot, CapturePlot plot, float maxTime)
    {
        var timeline = plot.Timeline;
        var capture = timeline.Capture;
        using var id = ImRaii.PushId(slot);
        ImGui.TextUnformatted($"{slot} ({capture.Label})");
        ImGui.TextDisabled($"0–{timeline.Duration:0.0}s, peak update per bar, {maxTime:0.0} ms scale");
        ImGui.InvisibleButton("##Timeline", new Vector2(Math.Max(1f, ImGui.GetContentRegionAvail().X), ImGui.GetFontSize() * 6));
        if (ImGui.IsItemVisible())
            DrawTimelineBars(plot, Math.Max(0.001f, maxTime));

        using (var node = ImRaii.TreeNode("Slowest updates"))
        {
            if (node)
            {
                foreach (var sample in timeline.Slowest)
                {
                    if (ImGui.Selectable($"{timeline.IntervalEnds[sample]:0.000}s   {capture.IntervalsMs[sample]:0.00} ms##{sample}", plot.Selected == sample))
                        plot.Selected = sample;
                }
            }
        }
        if (plot.Selected >= 0)
            DrawTimelineSelection(plot);

        if (capture.Markers.Length == 0)
            return;

        using var markers = ImRaii.TreeNode($"Markers ({capture.Markers.Length})###Markers");
        if (markers)
        {
            for (var i = 0; i < capture.Markers.Length; i++)
            {
                var marker = capture.Markers[i];
                var sample = timeline.SampleAt(marker.ElapsedSeconds);
                if (ImGui.Selectable($"{marker.ElapsedSeconds:0.000}s   {MarkerLabel(marker)}##{i}"))
                    plot.Selected = sample;
            }
            if (capture.DroppedMarkers > 0)
                ImGui.TextDisabled($"{capture.DroppedMarkers} markers exceeded the capture limit.");
        }
    }

    private static void DrawTimelineBars(CapturePlot plot, float maxTime)
    {
        var timeline = plot.Timeline;
        var min = ImGui.GetItemRectMin();
        var max = ImGui.GetItemRectMax();
        var step = (max.X - min.X) / timeline.Peaks.Length;
        var hovered = ImGui.IsItemHovered();
        var bucket = hovered ? Math.Clamp((int)((ImGui.GetMousePos().X - min.X) / step), 0, timeline.Peaks.Length - 1) : -1;
        var draw = ImGui.GetWindowDrawList();
        draw.AddRectFilled(min, max, ImGui.GetColorU32(ImGuiCol.FrameBg));
        var barColor = ImGui.GetColorU32(ImGuiCol.PlotHistogram);
        var highlight = ImGui.GetColorU32(ImGuiCol.PlotHistogramHovered);
        for (var i = 0; i < timeline.Peaks.Length; i++)
        {
            var sample = timeline.Peaks[i];
            if (sample < 0)
                continue;

            var left = min.X + i * step;
            var height = timeline.Capture.IntervalsMs[sample] / maxTime * (max.Y - min.Y);
            draw.AddRectFilled(new Vector2(left, max.Y - height), new Vector2(left + Math.Max(0.5f, step - 1), max.Y),
                bucket == i || sample == plot.Selected ? highlight : barColor);
        }

        var markerColor = ImGui.GetColorU32(ImGuiCol.Text);
        foreach (var marker in timeline.Capture.Markers)
        {
            var x = min.X + (max.X - min.X) * (float)Math.Clamp(marker.ElapsedSeconds / timeline.Duration, 0, 1);
            draw.AddLine(new Vector2(x, min.Y), new Vector2(x, min.Y + 7), markerColor);
        }
        for (var i = 0; i < timeline.GcBuckets.Length; i++)
        {
            if (timeline.GcBuckets[i])
            {
                var x = min.X + (i + 0.5f) * step;
                draw.AddLine(new Vector2(x, max.Y - 5), new Vector2(x, max.Y), markerColor);
            }
        }

        if (bucket >= 0 && timeline.Peaks[bucket] is >= 0 and var selected)
        {
            ImGui.SetTooltip($"{timeline.IntervalEnds[selected]:0.000}s   {timeline.Capture.IntervalsMs[selected]:0.00} ms. Click for details.");
            if (ImGui.IsItemClicked())
                plot.Selected = selected;
        }
    }

    private static void DrawTimelineSelection(CapturePlot plot)
    {
        var timeline = plot.Timeline;
        var capture = timeline.Capture;
        var selected = plot.Selected;
        var start = timeline.StartOf(selected);
        var end = timeline.IntervalEnds[selected];
        ImGui.TextUnformatted($"{start:0.000}s to {end:0.000}s ({capture.IntervalsMs[selected]:0.00} ms update interval)");
        if (selected < capture.NativeFrames.Length && capture.NativeFrames[selected] is { } native)
            ImGui.TextUnformatted($"Game frame {native.FrameNumber:N0}, {native.DeltaMs:0.00} ms from the last game tick");
        else
            ImGui.TextDisabled("No new game-frame reading at this update.");

        var from = Math.Max(0, start - 0.25);
        var through = end + 0.25;
        var markerIndex = 0;
        var gcIndex = timeline.FirstGcAt(from);
        var found = false;
        while (markerIndex < capture.Markers.Length || gcIndex < capture.GcObservations.Length)
        {
            var markerTime = markerIndex < capture.Markers.Length ? capture.Markers[markerIndex].ElapsedSeconds : double.PositiveInfinity;
            var gcTime = gcIndex < timeline.GcTimes.Length ? timeline.GcTimes[gcIndex] : double.PositiveInfinity;
            var time = Math.Min(markerTime, gcTime);
            if (time > through)
                break;

            if (markerTime <= gcTime)
            {
                var marker = capture.Markers[markerIndex++];
                if (time < from)
                    continue;
                ImGui.TextWrapped($"{time:0.000}s  {MarkerLabel(marker)}");
            }
            else
            {
                var gc = capture.GcObservations[gcIndex++];
                ImGui.TextUnformatted($"{time:0.000}s  GC changes (gen 0 / 1 / 2) {gc.Gen0}/{gc.Gen1}/{gc.Gen2}");
            }
            found = true;
        }
        if (!found)
            ImGui.TextDisabled("No GC changes or markers near this update.");
    }

    private static string? MarkerLabel(CaptureMarker marker)
        => marker.Addon is { } addon ? $"{addon.AddonName} ({AddonTab.FormatAddonEvent(addon.Event)})" : marker.Note;

    private sealed class CapturePlot(FrameTimeline timeline)
    {
        public FrameTimeline Timeline { get; } = timeline;
        public int Selected = -1;
    }

    private static void DrawGraphicsMemoryComparison(GraphicsMemoryCapture? before, GraphicsMemoryCapture? after)
    {
        if (before is null && after is null)
            return;

        ImGui.TextUnformatted("Graphics memory (process usage / budget)");
        var source = before?.End ?? before?.Start;
        var destination = after?.End ?? after?.Start;
        if (source is not null && destination is not null && source.AdapterId != destination.AdapterId)
            ImGui.TextColored(WarningColor, "The graphics adapter changed between runs.");
        if (before is { Start: null, End: null } || after is { Start: null, End: null })
            ImGui.TextDisabled("DXGI readings were unavailable for one or more runs.");

        using var table = ImRaii.Table("##GraphicsMemory", 4, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV);
        if (!table)
            return;

        ImGui.TableSetupColumn("Segment", ImGuiTableColumnFlags.WidthFixed, 100);
        ImGui.TableSetupColumn("Before (start → end)");
        ImGui.TableSetupColumn("After (start → end)");
        ImGui.TableSetupColumn("End usage change", ImGuiTableColumnFlags.WidthFixed, 126);
        ImGui.TableHeadersRow();
        ImGui.TableNextRow();
        DrawTableCell("Adapter");
        DrawTableCell(Adapter(before));
        DrawTableCell(Adapter(after));
        DrawTableCell("—");
        Row("Local", static sample => sample.LocalUsageBytes, static sample => sample.LocalBudgetBytes);
        Row("Non-local", static sample => sample.NonLocalUsageBytes, static sample => sample.NonLocalBudgetBytes);

        void Row(string label, Func<GraphicsMemorySnapshot, ulong?> usage, Func<GraphicsMemorySnapshot, ulong?> budget)
        {
            ImGui.TableNextRow();
            DrawTableCell(label);
            EndpointPair(before);
            EndpointPair(after);
            var delta = before?.End is { } left && after?.End is { } right && left.AdapterId == right.AdapterId &&
                usage(left) is <= (ulong)long.MaxValue and var leftBytes && usage(right) is <= (ulong)long.MaxValue and var rightBytes
                    ? DiagnosticFormatting.FormatSignedBytes((long)rightBytes - (long)leftBytes) : "—";
            DrawTableCell(delta);

            void EndpointPair(GraphicsMemoryCapture? capture)
            {
                ImGui.TableNextColumn();
                ImGui.TextWrapped(capture is null ? "—" : $"{Endpoint(capture.Start)} → {Endpoint(capture.End)}");
            }

            string Endpoint(GraphicsMemorySnapshot? sample) => sample is null || usage(sample) is null && budget(sample) is null
                ? "—" : $"{Bytes(usage(sample))} / {Bytes(budget(sample))}";
        }

        static string Adapter(GraphicsMemoryCapture? capture)
        {
            var first = capture?.Start;
            var last = capture?.End;
            return first is not null && last is not null && first.AdapterId != last.AdapterId
                ? $"{first.AdapterName} → {last.AdapterName}" : (last ?? first)?.AdapterName ?? "—";
        }

        static string Bytes(ulong? bytes) => bytes is <= (ulong)long.MaxValue and var known ? DiagnosticFormatting.FormatBytes((long)known) : "—";
    }
}
