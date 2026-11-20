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

    private readonly Dictionary<string, SubCommand> subCommands;

    public CommandHandler()
    {
        this.subCommands = new Dictionary<string, SubCommand>(StringComparer.OrdinalIgnoreCase)
        {
            ["help"] = new(_ => this.PrintHelp(), "Show this help message"),
            ["config"] = new(_ => ToggleConfig(), "Toggle the configuration window"),
            ["settings"] = new(_ => ToggleConfig(), "Toggle the configuration window"),
            ["save"] = new(_ => SaveConfig(), "Save configuration immediately"),
            ["reset"] = new(_ => ResetConfig(), "Reset configuration to defaults"),
        };

        Service.CommandManager.AddHandler(MainCommand, new CommandInfo(this.OnCommand)
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

        if (this.subCommands.TryGetValue(subCommandName, out var subCommand))
        {
            subCommand.Action(subArgs);
            return;
        }

        PrintChat($"Unknown command: {subCommandName}. Use '{MainCommand} help' for available commands.");
    }

    private static void HandleDefaultCommand()
    {
        ToggleConfig();
    }


    private static void ToggleConfig()
    {
        PluginState.ConfigAddon.Toggle();
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
        foreach (var (name, subCommand) in this.subCommands.OrderBy(pair => pair.Key))
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

    private sealed record SubCommand(Action<string> Action, string Description, string Usage = "");
}
