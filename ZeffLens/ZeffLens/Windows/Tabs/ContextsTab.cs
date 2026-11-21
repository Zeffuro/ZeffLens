using System;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using ZeffLens.Diagnostics;
using static ZeffLens.Windows.DiagnosticsWidgets;

namespace ZeffLens.Windows;

internal sealed class ContextsTab(MemoryDiagnosticsService diagnostics, PluginFilter filter)
{
    private readonly RowView<PluginContextSnapshot> _contextRows = new();

    public void Draw()
    {
        ImGui.InputText("Plugin filter", ref filter.Text, 128);
        var contexts = _contextRows.Filter(diagnostics.GetPluginContexts(), filter.Text, filter.Matches);

        if (contexts.Length == 0)
        {
            ImGui.TextUnformatted("No matching contexts.");
            return;
        }

        using (var table = ImRaii.Table("##PluginLoadContexts", 10, SortableTableFlags))
        {
            if (table)
            {
                ImGui.TableSetupColumn("Status", ImGuiTableColumnFlags.DefaultSort | ImGuiTableColumnFlags.PreferSortDescending);
                ImGui.TableSetupColumn("Internal name");
                ImGui.TableSetupColumn("Gen", ImGuiTableColumnFlags.PreferSortDescending);
                ImGui.TableSetupColumn("Context");
                ImGui.TableSetupColumn("ALC alive");
                ImGui.TableSetupColumn("GC checks", ImGuiTableColumnFlags.PreferSortDescending);
                ImGui.TableSetupColumn("Assemblies", ImGuiTableColumnFlags.PreferSortDescending);
                ImGui.TableSetupColumn("Last seen", ImGuiTableColumnFlags.PreferSortDescending);
                ImGui.TableSetupColumn("Retired", ImGuiTableColumnFlags.PreferSortDescending);
                ImGui.TableSetupColumn("Reason");
                ImGui.TableHeadersRow();

                contexts = _contextRows.Sort(static (context, column) => column switch
                {
                    0 => GetContextStatusRank(context),
                    1 => context.InternalName,
                    2 => context.Generation,
                    3 => context.ContextId,
                    4 => context.AssemblyLoadContextAlive,
                    5 => context.ForcedGcChecks,
                    6 => context.AssemblyCount,
                    7 => context.LastSeenUtc,
                    8 => context.RetiredUtc ?? DateTime.MinValue,
                    9 => context.RetiredReason ?? string.Empty,
                    _ => context.InternalName,
                });

                DrawClippedRows(contexts, static context =>
                {
                    ImGui.TableNextRow();

                    ImGui.TableNextColumn();
                    if (context.SuspectedRetainedAfterUnload)
                    {
                        ImGui.TextColored(ErrorColor, "alive after GC");
                    }
                    else if (context.IsRetired && context.AssemblyLoadContextAlive)
                    {
                        ImGui.TextColored(WarningColor, "retired/alive");
                    }
                    else if (context.IsRetired)
                    {
                        ImGui.TextColored(NeutralColor, "collected");
                    }
                    else
                    {
                        ImGui.TextUnformatted("loaded");
                    }

                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(context.InternalName);
                    if (ImGui.IsItemHovered())
                    {
                        ImGui.SetTooltip(context.Name);
                    }

                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(context.Generation.ToString());

                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted($"0x{context.ContextId:X}");

                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(context.AssemblyLoadContextAlive ? "yes" : "no");

                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(context.ForcedGcChecks.ToString());

                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(context.AssemblyCount.ToString());
                    if (ImGui.IsItemHovered() && context.AssemblyNames.Length > 0)
                    {
                        ImGui.SetTooltip(string.Join("\n", context.AssemblyNames));
                    }

                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(FormatTime(context.LastSeenUtc));

                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(context.RetiredUtc is null ? "-" : FormatTime(context.RetiredUtc.Value));

                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(context.RetiredReason ?? "-");
                    if (context.RetiredReason is { } reason && ImGui.IsItemHovered())
                        ImGui.SetTooltip(reason);
                });
            }
        }

        ImGui.TextWrapped(
            "A retained context means the plugin's collectible AssemblyLoadContext is still alive after an unload event. That usually means a remaining reference, event subscription, task, static cache, hook, or unmanaged callback. Run at least one settled snapshot with GC enabled before treating it as a real leak.");
    }
}
