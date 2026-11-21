using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Dalamud.Game.Command;
using ZeffLens.Configuration.Persistence;

namespace ZeffLens.Commands;

public sealed class CommandHandler : IDisposable
{
    private const string MainCommand = "/zefflens";
    private const string HelpDescription = "ZeffLens command. Use '/zefflens help' for options.";

    private readonly Dictionary<string, SubCommand> _subCommands;

    public CommandHandler()
    {
        _subCommands = new Dictionary<string, SubCommand>(StringComparer.OrdinalIgnoreCase)
        {
            ["help"] = new(_ => PrintHelp(), "Show this help message"),
            ["ui"] = new(_ => ToggleDiagnostics(), "Toggle the diagnostics window"),
            ["diag"] = new(_ => ToggleDiagnostics(), "Toggle the diagnostics window"),
            ["snapshot"] = new(_ => CaptureSnapshot(), "Capture a memory snapshot"),
            ["snap"] = new(_ => CaptureSnapshot(), "Capture a memory snapshot"),
            ["probe"] = new(_ => RefreshProbes(), "Refresh hook/static resource probes"),
            ["probes"] = new(_ => RefreshProbes(), "Refresh hook/static resource probes"),
            ["baseline"] = new(_ => SetBaseline(), "Set the current memory state as baseline"),
            ["gc"] = new(_ => ReleaseWorkingMemory(), "Force GC and trim if enabled"),
            ["release"] = new(_ => ReleaseWorkingMemory(), "Force GC and trim if enabled"),
            ["trim"] = new(_ => ReleaseWorkingMemory(collect: false), "Trim the game's working set"),
            ["record"] = new(Record, "Record a performance run", "<reference|candidate|cancel> [seconds] [label]"),
            ["mark"] = new(Mark, "Mark a moment in the current run", "[note]"),
            ["watchreload"] = new(WatchReload, "Take before and after samples around a plugin reload", "<plugin internal name>"),
            ["export"] = new(_ => ExportReport(), "Export diagnostics report JSON"),
            ["config"] = new(_ => ToggleDiagnostics(), "Toggle the diagnostics window"),
            ["settings"] = new(_ => ToggleDiagnostics(), "Toggle the diagnostics window"),
            ["save"] = new(_ => SaveConfig(), "Save configuration immediately"),
            ["reset"] = new(_ => ResetConfig(), "Reset configuration to defaults"),
        };

        Service.CommandManager.AddHandler(MainCommand, new CommandInfo(OnCommand)
        {
            DisplayOrder = 1,
            ShowInHelp = true,
            HelpMessage = HelpDescription,
        });
    }

    public void Dispose()
    {
        Service.CommandManager.RemoveHandler(MainCommand);
    }

    private void OnCommand(string command, string args)
    {
        var parts = args.Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
        {
            HandleDefaultCommand();
            return;
        }

        var subCommandName = parts[0];
        var subArgs = parts.Length > 1 ? parts[1] : string.Empty;

        if (_subCommands.TryGetValue(subCommandName, out var subCommand))
        {
            subCommand.Action(subArgs);
            return;
        }

        PrintChat($"Unknown command: {subCommandName}. Use '{MainCommand} help' for available commands.");
    }

    private static void HandleDefaultCommand()
    {
        ToggleDiagnostics();
    }

    private static void ToggleDiagnostics()
    {
        PluginState.DiagnosticsWindow?.Toggle();
    }

    private static void CaptureSnapshot()
    {
        var snapshot = PluginState.MemoryDiagnosticsService?.CaptureSnapshot("Command snapshot");
        PrintChat(snapshot is null
                      ? "Diagnostics service is not available."
                      : $"Snapshot #{snapshot.Id}: private={FormatBytes(snapshot.PrivateBytes)}, working={FormatBytes(snapshot.WorkingSetBytes)}, managed={FormatBytes(snapshot.ManagedLiveBytes)}.");
    }

    private static void SetBaseline()
    {
        var snapshot = PluginState.MemoryDiagnosticsService?.SetBaseline("Command baseline");
        PrintChat(snapshot is null
                      ? "Diagnostics service is not available."
                      : $"Baseline set at snapshot #{snapshot.Id}.");
    }

    private static void RefreshProbes()
    {
        var diagnostics = PluginState.MemoryDiagnosticsService;
        if (diagnostics is null)
            return;

        diagnostics.RequestResourceProbe();
        PrintChat("Resource refresh queued. See the Resources tab.");
    }

    private static void ReleaseWorkingMemory(bool collect = true)
    {
        if (PluginState.MemoryDiagnosticsService is not { } diagnostics)
            return;

        if (diagnostics.ReleaseWorkingMemory(collect) is not { } result)
        {
            PrintChat("Finish the active capture or reload first.");
            return;
        }

        if (result.EmptyWorkingSetCalled && !result.EmptyWorkingSetSucceeded)
            PrintChat($"Working-set trim failed (Win32 {result.LastWin32Error}).");
        PrintChat($"Immediately after: working set {FormatSignedBytes(result.After.WorkingSetBytes - result.Before.WorkingSetBytes)}, private commit {FormatSignedBytes(result.After.PrivateBytes - result.Before.PrivateBytes)}. See Memory tools for the follow-up.");
    }

    private static void ExportReport()
    {
        if (PluginState.MemoryDiagnosticsService is { } diagnostics)
            _ = ReportExport.WriteAsync(diagnostics);
        else
            PrintChat("Diagnostics service is not available.");
    }

    private static void Record(string args)
    {
        var parts = args.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var slot = parts.FirstOrDefault()?.ToLowerInvariant();
        if (PluginState.MemoryDiagnosticsService is not { } diagnostics)
            return;
        var frames = diagnostics.Frames;

        if (slot == "cancel")
        {
            PrintChat(frames.Cancel() ? "Recording cancelled." : "No running capture.");
            return;
        }

        if (diagnostics.ManagedProfiles.IsRunning || diagnostics.AddonProfiles.IsRunning || diagnostics.ReloadInProgress)
        {
            PrintChat("Wait for the current profile or plugin reload to finish.");
            return;
        }

        var seconds = 15;
        if (slot is not ("reference" or "candidate") ||
            (parts.Length > 1 && (!int.TryParse(parts[1], out seconds) || seconds is < 5 or > 60)))
        {
            PrintChat("Use /zefflens record <reference|candidate> [5–60 seconds] [label], or /zefflens record cancel.");
            return;
        }

        var label = parts.Length == 3 ? parts[2] : slot;
        PrintChat(frames.Start(slot == "reference", label, seconds)
            ? $"{seconds}s {slot} run starts in 3s."
            : "Already recording.");
    }

    private static void Mark(string note)
    {
        if (PluginState.MemoryDiagnosticsService?.Frames.Mark(note) != true)
            PrintChat("No running capture or marker limit reached.");
    }

    private static void WatchReload(string plugin)
    {
        if (string.IsNullOrWhiteSpace(plugin) ||
            plugin.Equals(Service.PluginInterface.InternalName, StringComparison.OrdinalIgnoreCase))
        {
            PrintChat("Use /zefflens watchreload <another plugin's internal name>.");
            return;
        }

        if (PluginState.MemoryDiagnosticsService?.RequestReloadCapture(Diagnostics.ReloadStage.Before, plugin, watchAfter: true) == true)
            PluginState.DiagnosticsWindow?.OpenReloadComparison();
        else
            PrintChat("A reload capture is already queued.");
    }

    private static void SaveConfig()
    {
        ConfigRepository.SaveImmediate(PluginState.Config);
        PrintChat("Configuration saved.");
    }

    private static void ResetConfig()
    {
        PluginState.Config = ConfigRepository.Reset();
        PrintChat("Configuration reset.");
    }

    private void PrintHelp()
    {
        var builder = new StringBuilder("ZeffLens Commands:\n");
        foreach (var (name, subCommand) in _subCommands.OrderBy(pair => pair.Key))
        {
            builder.Append(MainCommand).Append(' ').Append(name);
            if (!string.IsNullOrWhiteSpace(subCommand.Usage))
            {
                builder.Append(' ').Append(subCommand.Usage);
            }

            builder.Append(" - ").AppendLine(subCommand.Description);
        }

        PrintChat(builder.ToString());
    }

    private static void PrintChat(string message)
    {
        Service.ChatGui.Print(message, "ZeffLens");
    }

    private static string FormatBytes(long bytes)
        => Diagnostics.DiagnosticFormatting.FormatBytes(bytes);

    private static string FormatSignedBytes(long bytes)
        => Diagnostics.DiagnosticFormatting.FormatSignedBytes(bytes);

    private sealed record SubCommand(Action<string> Action, string Description, string Usage = "");
}
