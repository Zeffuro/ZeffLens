using System;
using System.Numerics;
using System.Text;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Interface.Utility.Raii;
using ZeffLens.Diagnostics;
using static ZeffLens.Windows.DiagnosticsWidgets;

namespace ZeffLens.Windows;

internal sealed class AddonTab(MemoryDiagnosticsService diagnostics)
{
    private string _addonName = string.Empty;
    private string _visibleAddonFilter = string.Empty;
    private bool _invalidAddonName;
    private readonly VisibleAddons _visibleAddons = new(Service.Framework, Service.Logger);

    private int _addonProfileSeconds = 15;
    private Task<string?>? _addonProfileRequest;

    public void Draw()
    {
        ImGui.SetNextItemWidth(200);
        var submitted = ImGui.InputTextWithHint("##AddonName", "Atk addon name", ref _addonName, 65, ImGuiInputTextFlags.EnterReturnsTrue);
        if (ImGui.IsItemEdited())
            _invalidAddonName = false;
        ImGui.SameLine();
        DrawVisibleAddonPicker();
        if (_invalidAddonName)
            ImGui.TextColored(WarningColor, "Enter one exact addon name.");

        using var tabs = ImRaii.TabBar("##AtkAddonTabs");
        if (!tabs)
            return;

        using (var tab = ImRaii.TabItem("Watch"))
        {
            if (tab)
            {
                DrawAddonWatch(submitted);
            }
        }
        using (var tab = ImRaii.TabItem("Profile"))
        {
            if (tab)
            {
                DrawAddonProfile(submitted);
            }
        }
    }

    private void DrawVisibleAddonPicker()
    {
        ImGui.SetNextItemWidth(150);
        using var combo = ImRaii.Combo("##VisibleAddons", "Visible addons");
        if (!combo)
            return;

        if (ImGui.IsWindowAppearing())
            _visibleAddons.Refresh();
        var (names, pending, error) = _visibleAddons.Read();
        ImGui.InputTextWithHint("##VisibleAddonFilter", "Find addon", ref _visibleAddonFilter, 65);
        using (ImRaii.Disabled(pending))
        {
            if (ImGui.SmallButton("Refresh list"))
                _visibleAddons.Refresh();
        }
        if (pending)
            ImGui.TextDisabled("Reading visible addons…");
        else if (error is not null)
            ImGui.TextColored(WarningColor, error);
        else if (names.Length == 0)
            ImGui.TextDisabled("No visible addons found.");

        foreach (var name in names)
        {
            if (name.Contains(_visibleAddonFilter, StringComparison.OrdinalIgnoreCase) && ImGui.Selectable(name, name == _addonName))
            {
                _addonName = name;
                _invalidAddonName = false;
            }
        }
    }

    private void DrawAddonWatch(bool submitted)
    {
        var (watch, entries) = diagnostics.Addons.Read();
        if (ImGui.Button("Watch addon") || submitted)
            _invalidAddonName = !diagnostics.Addons.Start(_addonName);
        if (watch is not null)
        {
            ImGui.SameLine();
            if (ImGui.Button("Stop"))
            {
                diagnostics.Addons.Stop();
                _invalidAddonName = false;
            }
        }

        if (watch is not null)
            ImGui.TextDisabled($"Watching {watch}");

        if (entries.Length == 0)
        {
            ImGui.TextDisabled("No setup, show, hide or finalize events yet.");
            return;
        }

        if (ImGui.Button("Copy events"))
        {
            var clipboard = new StringBuilder("Local time\tAddon\tEvent\n");
            foreach (var entry in entries)
                clipboard.AppendLine($"{entry.TimestampUtc.ToLocalTime():HH:mm:ss.fff}\t{entry.AddonName}\t{FormatAddonEvent(entry.Event)}");
            ImGui.SetClipboardText(clipboard.ToString());
        }
        ImGui.SameLine();
        if (ImGui.Button("Clear"))
            diagnostics.Addons.Clear();

        var count = Math.Min(64, entries.Length);
        ImGui.TextDisabled($"Last {count} of {entries.Length} events");
        using var table = ImRaii.Table("##AddonEvents", 3, ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY, Vector2.Zero);
        if (!table)
            return;

        ImGui.TableSetupColumn("Time");
        ImGui.TableSetupColumn("Addon");
        ImGui.TableSetupColumn("Event");
        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableHeadersRow();

        for (var i = entries.Length - 1; i >= entries.Length - count; i--)
        {
            var entry = entries[i];
            ImGui.TableNextRow();
            if (ImGui.TableNextColumn())
                ImGui.TextUnformatted($"{entry.TimestampUtc.ToLocalTime():HH:mm:ss.fff}");
            if (ImGui.TableNextColumn())
                ImGui.TextUnformatted(entry.AddonName);
            if (ImGui.TableNextColumn())
                ImGui.TextUnformatted(FormatAddonEvent(entry.Event));
        }
    }

    internal static string FormatAddonEvent(AddonEvent type) => type switch
    {
        AddonEvent.PostSetup => "Setup",
        AddonEvent.PostShow => "Show",
        AddonEvent.PostHide => "Hide",
        AddonEvent.PreFinalize => "Finalize",
        _ => type.ToString(),
    };

    private void DrawAddonProfile(bool submitted)
    {
        if (_addonProfileRequest is { IsCompleted: false })
        {
            ImGui.TextDisabled("Waiting for the framework update…");
            return;
        }

        var (activeName, remaining, result, error) = diagnostics.AddonProfiles.Read();
        if (activeName is not null)
        {
            ImGui.TextUnformatted($"Profiling {activeName} ({remaining:0.0}s left)");
            ImGui.SameLine();
            if (ImGui.Button("Stop"))
            {
                _addonProfileRequest = RunAddonProfileRequest(() =>
                {
                    diagnostics.Frames.NoteInterference();
                    diagnostics.AddonProfiles.Stop();
                    return null;
                });
            }
            return;
        }

        ImGui.SetNextItemWidth(100);
        if (ImGui.InputInt("Seconds", ref _addonProfileSeconds, 5))
            _addonProfileSeconds = Math.Clamp(_addonProfileSeconds, 5, 60);
        ImGui.SameLine();
        var busy = AddonProfileIsBusy();
        bool start;
        using (ImRaii.Disabled(busy))
        {
            start = ImGui.Button("Start profile");
        }
        if (!busy && (start || submitted))
        {
            var name = _addonName.Trim();
            _invalidAddonName = !AddonWatch.IsValidName(name);
            if (!_invalidAddonName)
            {
                var seconds = _addonProfileSeconds;
                _addonProfileRequest = RunAddonProfileRequest(() =>
                {
                    if (AddonProfileIsBusy())
                        return "Another diagnostic is running.";

                    diagnostics.Frames.NoteInterference();
                    diagnostics.AddonProfiles.Start(name, seconds);
                    return null;
                });
                return;
            }
        }

        if (_addonProfileRequest is { IsCompletedSuccessfully: true } request)
            error = request.Result ?? error;
        if (error is not null)
            ImGui.TextColored(WarningColor, error);
        if (result is null)
        {
            ImGui.TextDisabled("Profile one Atk addon, then reproduce the action.");
            return;
        }

        ImGui.SameLine();
        if (ExportButton("Export addon JSON"))
            _ = ReportExport.WriteAsync(diagnostics, result);
        ImGui.TextUnformatted($"{result.Name} ({result.Seconds:0.0}s)");
        DrawAddonNativeComparison(result.Before, result.After);
        if (result.Phases.Length == 0)
        {
            ImGui.TextDisabled("No complete calls recorded. Open or use the addon during the run.");
            return;
        }

        using (var table = ImRaii.Table("##AddonPhaseTiming", 6, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV))
        {
            if (table)
            {
                ImGui.TableSetupColumn("Phase");
                ImGui.TableSetupColumn("Calls");
                ImGui.TableSetupColumn("Total ms");
                ImGui.TableSetupColumn("Avg ms");
                ImGui.TableSetupColumn("P95 ms");
                ImGui.TableSetupColumn("Max ms");
                ImGui.TableHeadersRow();
                foreach (var phase in result.Phases)
                {
                    ImGui.TableNextRow();
                    DrawTableCell(FormatPhase(phase.Phase));
                    DrawTableCell(phase.Calls.ToString());
                    DrawTableCell($"{phase.TotalMs:0.000}");
                    DrawTableCell($"{phase.AverageMs:0.000}");
                    DrawTableCell($"{phase.P95Ms:0.000}");
                    if (phase.PercentileSamples < phase.Calls && ImGui.IsItemHovered())
                        ImGui.SetTooltip($"Last {phase.PercentileSamples} calls");
                    DrawTableCell($"{phase.MaxMs:0.000}");
                }
            }
        }

        ImGui.TextWrapped("Elapsed time includes the game call and nearby callbacks. Phases can overlap.");
        if (result.UnpairedEvents > 0)
            ImGui.TextDisabled($"Incomplete event pairs excluded: {result.UnpairedEvents}");
        using (var node = ImRaii.TreeNode("Slowest calls"))
        {
            if (node)
            {
                foreach (var call in result.Slowest)
                    ImGui.TextUnformatted($"{call.Seconds,6:0.000}s   {call.DurationMs,7:0.000} ms   {FormatPhase(call.Phase)}");
            }
        }
    }

    private bool AddonProfileIsBusy() =>
        diagnostics.Frames.IsRecording || diagnostics.ManagedProfiles.IsRunning || diagnostics.ReloadInProgress;

    private static async Task<string?> RunAddonProfileRequest(Func<string?> request)
    {
        var framework = Service.Framework;
        const string unavailable = "Addon profiling is unavailable.";
        try
        {
            return await framework.RunOnFrameworkThread(() =>
                framework.IsFrameworkUnloading || !framework.IsInFrameworkUpdateThread ? unavailable : request())
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return unavailable;
        }
        catch (Exception ex)
        {
            Service.Logger.Warning(ex, "Could not run an addon profiler request.");
            return "Could not run an addon profiler request. See the Dalamud log.";
        }
    }

    private static void DrawAddonNativeComparison(AddonNativeSnapshot? before, AddonNativeSnapshot? after)
    {
        using var table = ImRaii.Table("##AddonNativeComparison", 3, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV);
        if (!table)
            return;

        ImGui.TableSetupColumn("Native addon state");
        ImGui.TableSetupColumn("Before");
        ImGui.TableSetupColumn("After");
        ImGui.TableHeadersRow();

        DrawRow("Loaded instances", before?.LoadedInstances, after?.LoadedInstances);
        DrawRow("Visible instances", before?.VisibleInstances, after?.VisibleInstances);
        DrawRow("Node list entries", before?.NodeListEntries, after?.NodeListEntries);

        static void DrawRow(string label, int? beforeValue, int? afterValue)
        {
            ImGui.TableNextRow();
            DrawTableCell(label);
            DrawTableCell(beforeValue?.ToString("N0") ?? "Unavailable");
            DrawTableCell(afterValue?.ToString("N0") ?? "Unavailable");
        }
    }

    private static string FormatPhase(AddonPhase phase) => phase switch
    {
        AddonPhase.RequestedUpdate => "Requested update",
        AddonPhase.ReceiveEvent => "Receive event",
        _ => phase.ToString(),
    };
}
