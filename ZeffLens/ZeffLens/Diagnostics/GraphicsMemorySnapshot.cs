namespace ZeffLens.Diagnostics;

public sealed record GraphicsMemorySnapshot(string AdapterName, long AdapterId,
    ulong? LocalUsageBytes, ulong? LocalBudgetBytes, ulong? NonLocalUsageBytes, ulong? NonLocalBudgetBytes);

public sealed record GraphicsMemoryCapture(GraphicsMemorySnapshot? Start, GraphicsMemorySnapshot? End);
