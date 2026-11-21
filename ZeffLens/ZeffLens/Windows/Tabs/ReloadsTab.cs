using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using ZeffLens.Diagnostics;
using static ZeffLens.Windows.DiagnosticsWidgets;

namespace ZeffLens.Windows;

internal sealed class ReloadsTab(MemoryDiagnosticsService diagnostics, PluginFilter filter)
{
    private readonly RowView<PluginMemoryEvent> _reloadRows = new();

    private string? _reloadPlugin;
    private string _reloadSearch = string.Empty;
    private PluginResourceProbeSnapshot? _reloadBeforeResources;
    private HashSet<Guid>? _reloadBeforeHooks;

    private ReloadRun[]? _reloadTrendRuns;
    private string? _reloadTrendPlugin;
    private string[] _reloadTrendPlugins = [];

    public bool FocusRequested { get; set; }

    public void Draw()
    {
        using var tabs = ImRaii.TabBar("##ReloadTabs");
        if (!tabs)
            return;

        var flags = FocusRequested ? ImGuiTabItemFlags.SetSelected : ImGuiTabItemFlags.None;
        using (var tab = ImRaii.TabItem("Compare", flags))
        {
            if (tab)
            {
                FocusRequested = false;
                DrawReloadComparison();
            }
        }
        using (var tab = ImRaii.TabItem("History"))
        {
            if (tab)
            {
                DrawReloadHistory();
            }
        }
        using (var tab = ImRaii.TabItem("Trends"))
        {
            if (tab)
            {
                DrawReloadTrends();
            }
        }
    }

    private void DrawReloadHistory()
    {
        ImGui.InputText("Plugin filter", ref filter.Text, 128);
        var events = _reloadRows.Filter(diagnostics.GetEvents(), filter.Text, filter.Matches, newestFirst: true);
        if (events.Length == 0)
        {
            ImGui.TextUnformatted("No matching reload events.");
            return;
        }

        using (var table = ImRaii.Table("##PluginReloadEvents", 7, SortableTableFlags))
        {
            if (table)
            {
                ImGui.TableSetupColumn("Time", ImGuiTableColumnFlags.DefaultSort | ImGuiTableColumnFlags.PreferSortDescending);
                ImGui.TableSetupColumn("Kind");
                ImGui.TableSetupColumn("Plugin");
                ImGui.TableSetupColumn("Private", ImGuiTableColumnFlags.PreferSortDescending);
                ImGui.TableSetupColumn("Working", ImGuiTableColumnFlags.PreferSortDescending);
                ImGui.TableSetupColumn("Managed", ImGuiTableColumnFlags.PreferSortDescending);
                ImGui.TableSetupColumn("Sample");
                ImGui.TableHeadersRow();

                events = _reloadRows.Sort(static (pluginEvent, column) => column switch
                {
                    0 => pluginEvent.TimestampUtc,
                    1 => pluginEvent.Kind,
                    2 => string.Join(", ", pluginEvent.AffectedInternalNames),
                    3 => pluginEvent.Snapshot.PrivateBytes - (pluginEvent.PreviousSnapshot?.PrivateBytes ?? pluginEvent.Snapshot.PrivateBytes),
                    4 => pluginEvent.Snapshot.WorkingSetBytes - (pluginEvent.PreviousSnapshot?.WorkingSetBytes ?? pluginEvent.Snapshot.WorkingSetBytes),
                    5 => pluginEvent.Snapshot.ManagedLiveBytes - (pluginEvent.PreviousSnapshot?.ManagedLiveBytes ?? pluginEvent.Snapshot.ManagedLiveBytes),
                    6 => pluginEvent.Snapshot.Id,
                    _ => pluginEvent.TimestampUtc,
                });

                DrawClippedRows(events, static pluginEvent =>
                {
                    var previous = pluginEvent.PreviousSnapshot;
                    ImGui.TableNextRow();

                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(FormatTime(pluginEvent.TimestampUtc));

                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(pluginEvent.Kind.ToString());

                    ImGui.TableNextColumn();
                    var names = string.Join(", ", pluginEvent.AffectedInternalNames);
                    ImGui.TextUnformatted(names);
                    if (ImGui.IsItemHovered())
                        ImGui.SetTooltip(names);

                    ImGui.TableNextColumn();
                    DrawDeltaOnly(pluginEvent.Snapshot.PrivateBytes, previous?.PrivateBytes);

                    ImGui.TableNextColumn();
                    DrawDeltaOnly(pluginEvent.Snapshot.WorkingSetBytes, previous?.WorkingSetBytes);

                    ImGui.TableNextColumn();
                    DrawDeltaOnly(pluginEvent.Snapshot.ManagedLiveBytes, previous?.ManagedLiveBytes);
                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted($"#{pluginEvent.Snapshot.Id}");
                });
            }
        }

        ImGui.TextWrapped("Deltas are from the previous process sample. Events with the same sample share a measurement.");
    }

    private void DrawReloadComparison()
    {
        var (comparison, pending, error, watching, settling, automatic) = diagnostics.GetReloadView();
        var reloading = diagnostics.ReloadInProgress;
        var busy = pending || reloading;
        var recording = diagnostics.Frames.IsRecording || diagnostics.ManagedProfiles.IsRunning || diagnostics.AddonProfiles.IsRunning;
        if (error is not null)
            ImGui.TextColored(WarningColor, error);
        if (pending)
            ImGui.TextDisabled("Capture queued");

        if (comparison is null || comparison.Before.Active is not { } before)
        {
            DrawLoadedPluginPicker(diagnostics, "Reload target", ref _reloadPlugin, ref _reloadSearch);
            DrawReloadButton(_reloadPlugin, busy || recording, "Reload plugin");
            ImGui.SameLine();
            using (ImRaii.Disabled(busy || _reloadPlugin is null))
            {
                if (ImGui.Button("Watch reload"))
                    diagnostics.RequestReloadCapture(ReloadStage.Before, _reloadPlugin, watchAfter: true);
                ImGui.SameLine();
                if (ImGui.Button("Capture before"))
                    diagnostics.RequestReloadCapture(ReloadStage.Before, _reloadPlugin);
            }
            if (pending)
            {
                ImGui.SameLine();
                if (ImGui.Button("Cancel capture"))
                    diagnostics.CancelReloadCapture();
            }
            ImGui.TextWrapped("Reload plugin captures before and after. Use Watch reload if you want to reload from Dalamud yourself.");
            return;
        }

        ImGui.TextUnformatted(comparison.Plugin);
        if (reloading || (watching && !pending))
        {
            ImGui.SameLine();
            ImGui.TextDisabled(reloading ? "Reloading in Dalamud…" : settling || automatic ? "Waiting for the new load to settle" : "Ready. Reload the plugin in Dalamud.");
        }
        if (!watching || comparison.After is not null)
        {
            DrawReloadButton(comparison.Plugin, busy || recording, comparison.After is null ? "Reload plugin" : "Reload again");
            ImGui.SameLine();
            using (ImRaii.Disabled(busy))
            {
                if (ImGui.Button("Watch next reload"))
                    diagnostics.RequestReloadCapture(ReloadStage.Before, comparison.Plugin, watchAfter: true);
            }
            ImGui.SameLine();
            using (ImRaii.Disabled(busy))
            {
                if (ImGui.Button("Change plugin"))
                {
                    _reloadPlugin = null;
                    _reloadSearch = string.Empty;
                    diagnostics.ResetReloadComparison();
                    return;
                }
            }
        }
        using (ImRaii.Disabled(busy))
        {
            if (comparison.After is null)
            {
                if (ImGui.Button(comparison.Unloaded is null ? "Capture while disabled" : "Resample while disabled"))
                    diagnostics.RequestReloadCapture(ReloadStage.Unloaded);
                ImGui.SameLine();
            }
            if (ImGui.Button(comparison.After is null ? "Capture after" : "Recheck after"))
                diagnostics.RequestReloadCapture(ReloadStage.After);
            if (comparison.After is not null || comparison.Unloaded is not null)
            {
                ImGui.SameLine();
                if (ImGui.Button("Copy reload summary"))
                    ImGui.SetClipboardText(ReloadSummary.Format(comparison));
            }
            ImGui.SameLine();
            if (ExportButton("Export comparison JSON"))
                _ = ReportExport.WriteAsync(diagnostics, comparison);
        }
        ImGui.SameLine();
        if (pending)
        {
            if (ImGui.Button("Cancel capture"))
                diagnostics.CancelReloadCapture();
        }
        else if (watching && !reloading)
        {
            if (ImGui.Button("Stop watching"))
                diagnostics.StopReloadWatch();
        }

        ImGui.Separator();
        DrawReloadMemory(comparison);
        DrawReloadRegistries(comparison, before);
        DrawReloadDetails(comparison, before.Resources);
    }

    private void DrawReloadButton(string? plugin, bool busy, string label)
    {
        using (ImRaii.Disabled(busy || plugin is null || !DalamudPluginReload.IsAvailable))
        {
            if (ImGui.Button(label))
                diagnostics.RequestReloadCapture(ReloadStage.Before, plugin, reloadPlugin: true);
        }
        if (!DalamudPluginReload.IsAvailable && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip("Use Watch reload and reload from the Dalamud installer on this version.");
    }

    private static void DrawReloadMemory(ReloadComparison comparison)
    {
        var before = comparison.Before.Memory;
        var unloaded = comparison.Unloaded?.Memory;
        var after = comparison.After?.Memory;
        var columnCount = unloaded is null ? 4 : 5;
        using var table = ImRaii.Table("##ReloadMemory", columnCount, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV);
        if (!table)
            return;

        ImGui.TableSetupColumn("Process memory");
        ImGui.TableSetupColumn($"Before ({FormatTime(before.TimestampUtc)})");
        if (unloaded is not null)
            ImGui.TableSetupColumn($"Disabled ({FormatTime(unloaded.TimestampUtc)})");
        ImGui.TableSetupColumn(after is null ? "After" : $"After ({FormatTime(after.TimestampUtc)})");
        ImGui.TableSetupColumn("After − before");
        ImGui.TableHeadersRow();

        Row("Private RAM", static sample => sample.PrivateWorkingSetBytes);
        Row("Total RAM (working set)", static sample => sample.WorkingSetBytes);
        Row("Private commit", static sample => sample.PrivateBytes);
        Row("Managed heap used", static sample => sample.ManagedLiveBytes);
        Row("GC committed", static sample => sample.GcCommittedBytes);
        ImGui.TextWrapped("Private RAM is closest to Task Manager's Memory column. Private commit includes memory that can be outside RAM. All values cover the whole game.");

        void Row(string label, Func<MemorySnapshot, long?> bytes)
        {
            static string Format(long? size) => size is { } value ? DiagnosticFormatting.FormatBytes(value) : "—";
            var baseline = bytes(before);
            ImGui.TableNextRow();
            DrawTableCell(label);
            DrawTableCell(Format(baseline));
            if (unloaded is not null)
                DrawTableCell(Format(bytes(unloaded)));
            if (after is null)
            {
                DrawTableCell("—");
                DrawTableCell("—");
                return;
            }

            var latest = bytes(after);
            DrawTableCell(Format(latest));
            ImGui.TableNextColumn();
            DrawDeltaOnly(latest, baseline);
        }
    }

    private static void DrawReloadRegistries(ReloadComparison comparison, ReloadGeneration before)
    {
        var unloaded = comparison.Unloaded;
        var after = comparison.After;
        var columnCount = 2 + (unloaded is null ? 0 : 1) + (after is null ? 0 : 2);
        using var table = ImRaii.Table("##ReloadResources", columnCount, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV);
        if (!table)
            return;

        ImGui.TableSetupColumn("Registry");
        ImGui.TableSetupColumn($"Before (gen {before.Context.Generation})");
        if (unloaded is not null)
            ImGui.TableSetupColumn("Old while disabled");
        if (after is not null)
        {
            ImGui.TableSetupColumn("Old after reload");
            ImGui.TableSetupColumn($"New (gen {after.Active?.Context.Generation})");
        }
        ImGui.TableHeadersRow();
        Row("Hooks tracked", static probe => probe.HookCount);
        Row("Hooks enabled", static probe => probe.EnabledHookCount);
        Row("Hooks not disposed", static probe => probe.UndisposedHookCount);
        Row("Hook disposal unknown", static probe => probe.Hooks.Count(hook => hook.IsDisposed is null));
        Row("UI allocations", static probe => probe.KtkAllocatedNodeCount, KamiRegistries.Allocations);
        Row("UI nodes", static probe => probe.KtkCreatedNodeCount, KamiRegistries.Nodes);
        Row("UI addons", static probe => probe.KtkCreatedAddonCount, KamiRegistries.Addons);
        ImGui.TextDisabled("Hooks from Dalamud. UI entries from KamiToolKit. — unavailable, + partial.");

        void Row(string label, Func<PluginResourceProbeSnapshot, int> count, KamiRegistries registry = KamiRegistries.None)
        {
            ImGui.TableNextRow();
            DrawTableCell(label);
            DrawRegistryCount(before.Resources, count, registry, old: false);
            if (unloaded is not null)
                DrawRegistryCount(unloaded.Original?.Resources, count, registry, old: true);
            if (after is not null)
            {
                DrawRegistryCount(after.Original?.Resources, count, registry, old: true);
                DrawRegistryCount(after.Active?.Resources, count, registry, old: false);
            }
        }
    }

    private static void DrawRegistryCount(PluginResourceProbeSnapshot? probe,
        Func<PluginResourceProbeSnapshot, int> count, KamiRegistries registry, bool old)
    {
        ImGui.TableNextColumn();
        if (probe is null)
        {
            ImGui.TextDisabled("—");
            return;
        }

        var value = count(probe);
        var text = ReloadSummary.FormatCount(probe, value, registry);
        if (text == "—")
            ImGui.TextDisabled(text);
        else if (old && value > 0)
            ImGui.TextColored(WarningColor, text);
        else
            ImGui.TextUnformatted(text);
    }

    private void DrawReloadDetails(ReloadComparison comparison, PluginResourceProbeSnapshot? before)
    {
        if (!ReferenceEquals(before, _reloadBeforeResources))
        {
            _reloadBeforeResources = before;
            _reloadBeforeHooks = before?.Hooks.Where(hook => hook.RegistryId.HasValue)
                .Select(hook => hook.RegistryId.GetValueOrDefault()).ToHashSet();
        }

        var latest = comparison.After ?? comparison.Unloaded;
        if (latest is not null)
        {
            var original = latest.Original?.Context;
            ImGui.TextUnformatted(original switch
            {
                null => "Old context is no longer tracked.",
                { AssemblyLoadContextAlive: false } => $"Old context (gen {original.Generation}) was collected.",
                { SuspectedRetainedAfterUnload: true } => $"Old context (gen {original.Generation}) is still alive after GC.",
                _ => $"Old context (gen {original.Generation}) is still alive.",
            });
            DrawReloadGeneration("Old load details", latest.Original, _reloadBeforeHooks);
            if (comparison.After is not null)
                DrawReloadGeneration("New load details", latest.Active);
            if (comparison.Unloaded is { } disabled && comparison.After is not null)
                DrawReloadGeneration("Old load while disabled", disabled.Original, _reloadBeforeHooks);

            if (latest.OtherPluginChanges.Length > 0)
            {
                var names = string.Join(", ", latest.OtherPluginChanges.Take(4));
                var remaining = latest.OtherPluginChanges.Length - 4;
                ImGui.TextWrapped(remaining > 0 ? $"Other plugins changed: {names} (+{remaining})" : $"Other plugins changed: {names}");
            }
        }

        DrawReloadGeneration("Before details", comparison.Before.Active);
        if (comparison.Before.ProbeErrors.Length > 0)
            DrawReloadProbeErrors("Before probe issues", comparison.Before.ProbeErrors);
        if (comparison.Unloaded is { ProbeErrors.Length: > 0 } unloaded)
            DrawReloadProbeErrors("Disabled probe issues", unloaded.ProbeErrors);
        if (comparison.After is { ProbeErrors.Length: > 0 } after)
            DrawReloadProbeErrors("After probe issues", after.ProbeErrors);
    }

    private static void DrawReloadGeneration(string label, ReloadGeneration? generation, IReadOnlySet<Guid>? before = null)
    {
        if (generation?.Resources is not { } probe)
            return;

        using var node = ImRaii.TreeNode(label);
        if (!node)
            return;

        if (probe.Hooks.Length == 0 && probe.NativeNodes.Length == 0 && probe.StaticFields.Length == 0)
            ImGui.TextDisabled("No entries observed.");
        ResourcesTab.DrawHookProbeDetails(probe, before, showAll: true);
        ResourcesTab.DrawNativeNodeProbeDetails(probe, showAll: true);
        ResourcesTab.DrawStaticFieldProbeDetails(probe);
        ResourcesTab.DrawProbeErrors(probe);
    }

    private static void DrawReloadProbeErrors(string label, string[] errors)
    {
        using var node = ImRaii.TreeNode(label);
        if (!node)
            return;
        foreach (var error in errors)
            ImGui.TextWrapped(error);
    }

    private void DrawReloadTrends()
    {
        var runs = diagnostics.GetReloadRuns();
        if (!ReferenceEquals(runs, _reloadTrendRuns))
        {
            _reloadTrendRuns = runs;
            var names = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var run in runs)
                names.Add(run.Plugin);
            _reloadTrendPlugins = [.. names];
            Array.Sort(_reloadTrendPlugins, StringComparer.OrdinalIgnoreCase);
            if (runs.Length > 0 && (_reloadTrendPlugin is null || !names.Contains(_reloadTrendPlugin)))
                _reloadTrendPlugin = runs[^1].Plugin;
        }

        if (runs.Length == 0)
        {
            ImGui.TextWrapped("Complete a reload in Compare to start a trend. Use Reload again to add another run.");
            return;
        }

        ImGui.SetNextItemWidth(240);
        using (var combo = ImRaii.Combo("Plugin", _reloadTrendPlugin ?? "Choose a plugin"))
        {
            if (combo)
            {
                foreach (var plugin in _reloadTrendPlugins)
                {
                    if (ImGui.Selectable(plugin, plugin == _reloadTrendPlugin))
                        _reloadTrendPlugin = plugin;
                }
            }
        }
        ImGui.SameLine();
        if (ExportButton("Export trend JSON"))
            _ = ReportExport.WriteAsync(diagnostics, runs.Where(run => run.Plugin.Equals(_reloadTrendPlugin, StringComparison.OrdinalIgnoreCase)).ToArray());
        ImGui.TextWrapped("Old hooks and UI nodes are from that run’s unloaded generation. The context count includes all tracked old generations of this plugin.");

        using var table = ImRaii.Table("##ReloadTrends", 8, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV);
        if (!table)
            return;

        ImGui.TableSetupColumn("Checked");
        ImGui.TableSetupColumn("Generation");
        ImGui.TableSetupColumn("Old context");
        ImGui.TableSetupColumn("Old contexts alive");
        ImGui.TableSetupColumn("Old hooks");
        ImGui.TableSetupColumn("Old UI nodes");
        ImGui.TableSetupColumn("Private commit");
        ImGui.TableSetupColumn("Change");
        ImGui.TableHeadersRow();

        foreach (var run in runs)
        {
            if (!run.Plugin.Equals(_reloadTrendPlugin, StringComparison.OrdinalIgnoreCase))
                continue;

            ImGui.TableNextRow();
            DrawTableCell(FormatTime(run.CheckedUtc));
            DrawTableCell($"{run.BeforeGeneration} → {run.AfterGeneration}");
            DrawTableCell(run.OriginalContextAlive switch { true => "Alive", false => "Collected", _ => "—" });
            DrawTableCell(run.RetiredContextsAlive?.ToString() ?? "—");
            DrawTableCell(FormatReloadTrendCount(run.OldHooks));
            DrawTableCell(FormatReloadTrendCount(run.OldNodes));
            DrawTableCell(DiagnosticFormatting.FormatBytes(run.PrivateCommitBytes));
            DrawTableCell(DiagnosticFormatting.FormatSignedBytes(run.PrivateCommitChangeBytes));
            if (run.OtherPluginsChanged && ImGui.IsItemHovered())
                ImGui.SetTooltip("Other plugins changed during this run.");
        }
    }

    private static string FormatReloadTrendCount(ReloadRunCount? count)
        => count is { Complete: true } known ? known.Count.ToString() : count is { Count: > 0 } partial ? $"{partial.Count}+" : "—";
}
