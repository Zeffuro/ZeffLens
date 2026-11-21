using System;
using System.Linq;
using System.Text;

namespace ZeffLens.Diagnostics;

internal static class ReloadSummary
{
    public static string Format(ReloadComparison comparison)
    {
        var text = new StringBuilder().AppendLine($"ZeffLens reload for {comparison.Plugin}");
        var before = comparison.Before.Memory;
        var unloaded = comparison.Unloaded?.Memory;
        var after = comparison.After?.Memory;
        text.Append("Process memory (before");
        if (unloaded is not null)
            text.Append(" → disabled");
        if (after is not null)
            text.Append(" → after");
        text.AppendLine(")");
        Memory("Private RAM", static sample => sample.PrivateWorkingSetBytes);
        Memory("Total RAM (working set)", static sample => sample.WorkingSetBytes);
        Memory("Private commit", static sample => sample.PrivateBytes);
        Memory("Managed heap used", static sample => sample.ManagedLiveBytes);
        Memory("GC committed", static sample => sample.GcCommittedBytes);

        text.AppendLine().AppendLine("Tracked resources (— unavailable, + partial)");
        Resources("Before", comparison.Before.Active);
        if (comparison.Unloaded is { } disabled)
            Resources("Old while disabled", disabled.Original);
        if (comparison.After is { } reloaded)
        {
            Resources("Old after reload", reloaded.Original);
            Resources("New after reload", reloaded.Active);
        }

        var latest = comparison.After ?? comparison.Unloaded;
        if (latest?.Original?.Resources is { } old)
        {
            foreach (var group in old.Hooks.GroupBy(item => (item.DelegateType, item.IsDisposed))
                         .OrderBy(item => item.Key.IsDisposed == true).Take(12))
            {
                var state = group.Key.IsDisposed switch { true => "disposed", false => "live", null => "disposal unknown" };
                text.AppendLine($"- {group.Count()}× hook {group.Key.DelegateType} ({state})");
            }
            foreach (var group in old.NativeNodes.GroupBy(item => (item.TrackerName, item.NodeType)).Take(12))
                text.AppendLine($"- {group.Count()}× {group.Key.TrackerName}: {group.Key.NodeType}");
            if (old.Hooks.Length > 0 || old.NativeNodes.Length > 0)
                text.AppendLine("JSON includes the full captured resource details.");
        }

        if (latest is { OtherPluginChanges.Length: > 0 })
            text.AppendLine($"Other plugins changed: {string.Join(", ", latest.OtherPluginChanges)}");
        if (HasProbeErrors(comparison.Before) || HasProbeErrors(comparison.Unloaded) || HasProbeErrors(comparison.After))
            text.AppendLine("A registry scan reported issues. See JSON for details.");
        return text.ToString();

        void Memory(string label, Func<MemorySnapshot, long?> bytes)
        {
            static string FormatSize(long? size) => size is { } amount ? DiagnosticFormatting.FormatBytes(amount) : "—";
            var value = bytes(before);
            text.Append("- ").Append(label).Append(": ").Append(FormatSize(value));
            if (unloaded is not null)
                text.Append(" → ").Append(FormatSize(bytes(unloaded)));
            if (after is not null)
            {
                var latest = bytes(after);
                text.Append(" → ").Append(FormatSize(latest));
                if (latest - value is { } delta)
                    text.Append(" (").Append(DiagnosticFormatting.FormatSignedBytes(delta)).Append(')');
            }
            text.AppendLine();
        }

        void Resources(string label, ReloadGeneration? load)
        {
            if (load is null)
            {
                text.AppendLine($"- {label}: context unavailable");
                return;
            }

            var context = load.Context;
            var state = context.AssemblyLoadContextAlive ? context.IsRetired ? "still alive" : "active" : "collected";
            var probe = load.Resources;
            text.AppendLine($"- {label}, gen {context.Generation} ({state}): " +
                $"hooks {FormatCount(probe, probe?.HookCount ?? 0)}, " +
                $"UI allocations {FormatCount(probe, probe?.KtkAllocatedNodeCount ?? 0, KamiRegistries.Allocations)}, " +
                $"nodes {FormatCount(probe, probe?.KtkCreatedNodeCount ?? 0, KamiRegistries.Nodes)}, " +
                $"addons {FormatCount(probe, probe?.KtkCreatedAddonCount ?? 0, KamiRegistries.Addons)}");
        }
    }

    public static string FormatCount(PluginResourceProbeSnapshot? probe, int value, KamiRegistries registry = KamiRegistries.None)
    {
        if (probe is null)
            return "—";

        var complete = registry == KamiRegistries.None ? probe.ReadHookRegistry : (probe.ReadKamiRegistries & registry) != 0;
        return complete ? value.ToString() : value == 0 ? "—" : $"{value}+";
    }

    private static bool HasProbeErrors(ReloadCheckpoint? checkpoint)
        => checkpoint?.ProbeErrors.Length > 0 || checkpoint?.Active?.Resources?.Errors.Length > 0 ||
           checkpoint?.Original?.Resources?.Errors.Length > 0;
}
