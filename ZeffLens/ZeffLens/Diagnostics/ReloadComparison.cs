using System;

namespace ZeffLens.Diagnostics;

public sealed record ReloadGeneration(PluginContextSnapshot Context, PluginResourceProbeSnapshot? Resources)
{
    internal bool IsSameLoad(PluginContextSnapshot context)
        => Context.Generation == context.Generation && Context.ContextId == context.ContextId &&
           Context.InternalName.Equals(context.InternalName, StringComparison.OrdinalIgnoreCase);

    internal bool Matches(PluginResourceProbeSnapshot resources)
        => Context.Generation == resources.Generation && Context.ContextId == resources.ContextId &&
           Context.IsRetired == resources.IsRetiredContext &&
           Context.InternalName.Equals(resources.InternalName, StringComparison.OrdinalIgnoreCase);
}

public sealed record ReloadCheckpoint(
    MemorySnapshot Memory,
    ReloadGeneration? Active,
    ReloadGeneration? Original,
    string[] ProbeErrors,
    string[] OtherPluginChanges)
{
    public int LastEventId { get; init; }
    public int? RetiredContextsAlive { get; init; }
}

public sealed record ReloadRun(
    string Plugin, int BeforeSampleId, DateTime CheckedUtc, int BeforeGeneration, int AfterGeneration,
    int? RetiredContextsAlive, bool? OriginalContextAlive, ReloadRunCount? OldHooks, ReloadRunCount? OldNodes,
    long PrivateCommitBytes, long PrivateCommitChangeBytes, bool OtherPluginsChanged);

public readonly record struct ReloadRunCount(int Count, bool Complete);

public sealed record ReloadComparison(string Plugin, ReloadCheckpoint Before, ReloadCheckpoint? Unloaded, ReloadCheckpoint? After)
{
    internal string? Validate(ReloadStage stage, PluginContextSnapshot? active)
    {
        if (stage == ReloadStage.Unloaded)
            return active is null ? null : "Disable the plugin before taking the unloaded sample.";

        if (active is null)
            return "Enable the plugin before taking the after sample.";
        if (Before.Active?.IsSameLoad(active) == true)
            return "The original load is still active. Reload the plugin first.";
        if (After?.Active is { } previous && !previous.IsSameLoad(active))
            return "The plugin reloaded again. Start a new comparison.";
        return null;
    }
}

internal enum ReloadStage
{
    Before,
    Unloaded,
    After,
}
