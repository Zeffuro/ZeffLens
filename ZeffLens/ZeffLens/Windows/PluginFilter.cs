using System;
using System.Linq;
using ZeffLens.Diagnostics;

namespace ZeffLens.Windows;

internal sealed class PluginFilter
{
    public string Text = string.Empty;

    public bool Matches(PluginMemoryEvent entry)
        => string.IsNullOrWhiteSpace(Text) || entry.AffectedInternalNames.Any(Matches);

    public bool Matches(PluginContextSnapshot context) => Matches(context.InternalName) || Matches(context.Name);

    public bool Matches(PluginResourceProbeSnapshot probe) => Matches(probe.InternalName) || Matches(probe.Name);

    private bool Matches(string name) => string.IsNullOrWhiteSpace(Text) || name.Contains(Text, StringComparison.OrdinalIgnoreCase);
}
