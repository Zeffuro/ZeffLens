using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.CompilerServices;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using ZeffLens.Diagnostics;
using static ZeffLens.Windows.DiagnosticsWidgets;

namespace ZeffLens.Windows;

internal sealed class ResourcesTab(MemoryDiagnosticsService diagnostics, PluginFilter filter)
{
    private static readonly ConditionalWeakTable<PluginResourceProbeSnapshot, NativeNodeGroup[]> NativeNodeGroups = new();
    private readonly RowView<PluginResourceProbeSnapshot> _resourceRows = new();

    public void Draw()
    {
        ImGui.InputText("Plugin filter", ref filter.Text, 128);
        var (source, timestamp) = diagnostics.GetResourceView();
        var probes = _resourceRows.Filter(source, filter.Text, filter.Matches);

        if (ImGui.Button("Refresh"))
            diagnostics.RequestResourceProbe();
        ImGui.SameLine();
        if (diagnostics.ProbePending)
            ImGui.TextDisabled("Refresh queued");
        else if (timestamp is { } updatedAt)
            ImGui.TextDisabled($"Updated {FormatTime(updatedAt)}");

        ImGui.TextDisabled("Counts are per Dalamud or KamiToolKit registry.");

        if (probes.Length == 0)
        {
            ImGui.Separator();
            ImGui.TextUnformatted("No resource entries. Refresh after loading or unloading a plugin.");
            return;
        }

        ImGui.Separator();
        var available = ImGui.GetContentRegionAvail();
        var tableHeight = Math.Clamp(available.Y * 0.42f, 160.0f, 300.0f);
        DrawResourceProbeTable(ref probes, new Vector2(0.0f, tableHeight));

        ImGui.Separator();
        using var child = ImRaii.Child("##ResourceProbeDetails", Vector2.Zero, true, ImGuiWindowFlags.HorizontalScrollbar);
        if (!child)
            return;

        foreach (var probe in probes)
        {
            var label = $"{probe.InternalName} gen {probe.Generation} - hooks {probe.HookCount}, " +
                $"KTK alloc {probe.KtkAllocatedNodeCount}, managed {probe.KtkCreatedNodeCount}, addons {probe.KtkCreatedAddonCount}, " +
                $"static refs {probe.NativeUiCandidateCount}##probe-{probe.InternalName}-{probe.ContextId}-{probe.Generation}";
            using var node = ImRaii.TreeNode(label);
            if (!node)
                continue;

            DrawNativeNodeProbeDetails(probe);
            DrawHookProbeDetails(probe);
            DrawStaticFieldProbeDetails(probe);
            DrawProbeErrors(probe);
        }
    }

    private void DrawResourceProbeTable(ref PluginResourceProbeSnapshot[] probes, Vector2 outerSize)
    {
        using var table = ImRaii.Table("##ResourceProbes", 10, ScrollableSortableTableFlags, outerSize);
        if (!table)
        {
            return;
        }

        ImGui.TableSetupColumn("Plugin");
        ImGui.TableSetupColumn("Gen", ImGuiTableColumnFlags.PreferSortDescending);
        ImGui.TableSetupColumn("State");
        ImGui.TableSetupColumn("Tracked hooks", ImGuiTableColumnFlags.PreferSortDescending);
        ImGui.TableSetupColumn("Enabled", ImGuiTableColumnFlags.PreferSortDescending);
        ImGui.TableSetupColumn("Live hooks", ImGuiTableColumnFlags.PreferSortDescending);
        ImGui.TableSetupColumn("Disposable fields", ImGuiTableColumnFlags.PreferSortDescending);
        ImGui.TableSetupColumn("KTK alloc/node/addon", ImGuiTableColumnFlags.PreferSortDescending);
        ImGui.TableSetupColumn("UI field types", ImGuiTableColumnFlags.PreferSortDescending);
        ImGui.TableSetupColumn("Probe issues", ImGuiTableColumnFlags.PreferSortDescending);
        ImGui.TableHeadersRow();

        probes = _resourceRows.Sort(static (probe, column) => column switch
        {
            0 => probe.InternalName,
            1 => probe.Generation,
            2 => GetProbeStateRank(probe),
            3 => probe.HookCount,
            4 => probe.EnabledHookCount,
            5 => probe.UndisposedHookCount,
            6 => probe.StaticDisposableFieldCount,
            7 => probe.KtkAllocatedNodeCount,
            8 => probe.NativeUiCandidateCount,
            9 => probe.Errors.Length,
            _ => probe.InternalName,
        });

        DrawClippedRows(probes, static probe =>
        {
            ImGui.TableNextRow();

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(probe.InternalName);
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip(probe.Name);
            }

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(probe.Generation.ToString());

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(probe.IsRetiredContext ? "retired" : probe.IsLoaded ? "loaded" : "unknown");

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(probe.HookCount.ToString());

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(probe.EnabledHookCount.ToString());

            ImGui.TableNextColumn();
            ImGui.TextColored(probe.IsRetiredContext && probe.UndisposedHookCount > 0 ? WarningColor : NeutralColor, probe.UndisposedHookCount.ToString());

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(probe.StaticDisposableFieldCount.ToString());

            ImGui.TableNextColumn();
            ImGui.TextColored(
                probe.IsRetiredContext && (probe.KtkAllocatedNodeCount > 0 || probe.KtkCreatedNodeCount > 0 || probe.KtkCreatedAddonCount > 0) ? WarningColor : NeutralColor,
                $"{probe.KtkAllocatedNodeCount}/{probe.KtkCreatedNodeCount}/{probe.KtkCreatedAddonCount}");
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip("KamiToolKit AllocatedNodes / CreatedNodes / CreatedAddons");
            }

            ImGui.TableNextColumn();
            ImGui.TextColored(NeutralColor, probe.NativeUiCandidateCount.ToString());
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip("Targeted static fields that look native-UI related. Declared types only.");
            }

            ImGui.TableNextColumn();
            ImGui.TextColored(probe.Errors.Length > 0 ? WarningColor : NeutralColor, probe.Errors.Length.ToString());
            if (ImGui.IsItemHovered() && probe.Errors.Length > 0)
            {
                ImGui.SetTooltip(string.Join("\n", probe.Errors.Take(12)));
            }
        });
    }

    internal static void DrawNativeNodeProbeDetails(PluginResourceProbeSnapshot probe, bool showAll = false)
    {
        if (probe.NativeNodes.Length == 0)
            return;

        if (probe.IsRetiredContext && probe.KtkAllocatedNodeCount > 0)
            ImGui.TextColored(WarningColor, "Native allocation entries remain after unload.");
        else if (probe.IsRetiredContext)
            ImGui.TextColored(WarningColor, "Managed registry entries remain after unload.");

        ImGui.TextUnformatted($"{probe.KtkAllocatedNodeCount} allocations, {probe.KtkCreatedNodeCount} registered nodes, {probe.KtkCreatedAddonCount} addons");
        var entries = probe.KtkAllocatedNodeCount + probe.KtkCreatedNodeCount + probe.KtkCreatedAddonCount;
        if (probe.NativeNodes.Length < entries)
            ImGui.TextDisabled($"Details shown for {probe.NativeNodes.Length} of {entries} registry entries.");

        var groups = NativeNodeGroups.GetValue(probe, BuildNativeNodeGroups);
        var groupCount = showAll ? groups.Length : Math.Min(groups.Length, 80);
        for (var index = 0; index < groupCount; index++)
        {
            var group = groups[index];
            using var open = ImRaii.TreeNode(group.Label);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(group.Tooltip);

            if (!open)
                continue;

            var count = showAll ? group.Addresses.Length : Math.Min(group.Addresses.Length, 30);
            for (var row = 0; row < count; row++)
                ImGui.TextColored(group.Color, group.Addresses[row]);

            if (count < group.Addresses.Length)
                ImGui.TextColored(NeutralColor, $"... {group.Addresses.Length - count} more");
        }
    }

    private static NativeNodeGroup[] BuildNativeNodeGroups(PluginResourceProbeSnapshot probe)
        => probe.NativeNodes
            .GroupBy(node => (node.TrackerName, node.NodeType, node.IsDisposed, node.IsAddonRootNode, node.IsOverlayAddon, node.InternalName))
            .OrderBy(group => group.Key.TrackerName, StringComparer.OrdinalIgnoreCase)
            .ThenByDescending(group => group.Count())
            .ThenBy(group => group.Key.NodeType, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var first = group.First();
                var name = string.IsNullOrWhiteSpace(first.InternalName) ? string.Empty : $" {first.InternalName}";
                var color = probe.IsRetiredContext && (first.TrackerName == "AllocatedNodes" || first.IsDisposed == false)
                    ? WarningColor : NeutralColor;
                var label = $"{group.Count()}x {TrackerDisplayName(first.TrackerName)}: {ShortTypeName(first.NodeType)}{name} disposed={first.IsDisposed?.ToString() ?? "?"}" +
                    $"##native-node-group-{probe.ContextId}-{probe.Generation}-{group.Key}";
                var tooltip = $"{first.TrackerName}\n{first.NodeType}\n{first.AssemblyName}\naddon root: {first.IsAddonRootNode?.ToString() ?? "?"}\noverlay addon: {first.IsOverlayAddon?.ToString() ?? "?"}";
                var addresses = group.Select(node => string.IsNullOrWhiteSpace(node.Address) ? "(no address)" : node.Address).ToArray();
                return new NativeNodeGroup(label, tooltip, color, addresses);
            })
            .ToArray();

    private sealed record NativeNodeGroup(string Label, string Tooltip, Vector4 Color, string[] Addresses);

    internal static void DrawHookProbeDetails(PluginResourceProbeSnapshot probe, IReadOnlySet<Guid>? before = null, bool showAll = false)
    {
        if (probe.Hooks.Length == 0)
        {
            return;
        }

        ImGui.TextUnformatted("Hooks:");
        foreach (var hook in probe.Hooks.Take(showAll ? int.MaxValue : 40))
        {
            var color = probe.IsRetiredContext && hook.IsDisposed == false ? WarningColor : NeutralColor;
            var seen = hook.RegistryId is { } id && before?.Contains(id) == true ? " (seen before)" : string.Empty;
            ImGui.TextColored(color, $"{hook.Address} {hook.BackendName} enabled={hook.IsEnabled?.ToString() ?? "?"} disposed={hook.IsDisposed?.ToString() ?? "?"}{seen}");
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip($"{hook.HookType}\n{hook.DelegateType}\n{hook.AssemblyName}");
            }
        }
        if (!showAll && probe.Hooks.Length > 40)
            ImGui.TextDisabled($"Showing 40 of {probe.Hooks.Length}. Export JSON for the full list.");
    }

    internal static void DrawStaticFieldProbeDetails(PluginResourceProbeSnapshot probe)
    {
        if (probe.StaticFields.Length == 0)
        {
            return;
        }

        ImGui.TextUnformatted("Static field declarations");
        foreach (var field in probe.StaticFields.Take(80))
        {
            ImGui.TextUnformatted($"{field.DeclaringType}.{field.FieldName}: {field.FieldType}");
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip($"{field.AssemblyName}\nfield type: {field.FieldType}\nnative UI candidate: {field.IsNativeUiCandidate}");
            }
        }
    }

    internal static void DrawProbeErrors(PluginResourceProbeSnapshot probe)
    {
        if (probe.Errors.Length == 0)
        {
            return;
        }

        ImGui.TextUnformatted("Probe errors:");
        foreach (var error in probe.Errors.Take(20))
        {
            ImGui.TextColored(WarningColor, error);
        }
    }

    private static string ShortTypeName(string typeName)
    {
        var lastDot = typeName.LastIndexOf('.');
        return lastDot < 0 || lastDot == typeName.Length - 1
                   ? typeName
                   : typeName[(lastDot + 1)..];
    }

    private static string TrackerDisplayName(string trackerName)
        => trackerName switch
        {
            "AllocatedNodes" => "native allocation",
            "CreatedNodes" => "managed node",
            "CreatedAddons" => "managed addon",
            _ => trackerName,
        };
}
