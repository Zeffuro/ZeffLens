using System.Runtime.Loader;

namespace ZeffLens;

internal sealed record ProbeOwner(
    string Key,
    string InternalName,
    string Name,
    int Generation,
    bool IsLoaded,
    bool IsRetiredContext,
    string? LoadContextName,
    int ContextId)
{
    public static ProbeOwner Global { get; } = new(
        "<global>",
        "<global>",
        "<global>",
        0,
        IsLoaded: true,
        IsRetiredContext: false,
        LoadContextName: null,
        ContextId: 0);

    public static string BuildKey(string internalName, int contextId, int generation)
        => $"{internalName}|{contextId}|{generation}";
}

internal sealed record ProbeTarget(ProbeOwner Owner, AssemblyLoadContext Context);
