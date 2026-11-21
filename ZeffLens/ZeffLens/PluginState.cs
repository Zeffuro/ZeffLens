using ZeffLens.Configuration;
using ZeffLens.Commands;
using ZeffLens.Windows;

namespace ZeffLens;

public static class PluginState
{
    public static SystemConfiguration Config { get; set; } = null!;

    public static CommandHandler? CommandHandler { get; set; }

    public static DtrService? DtrService { get; set; }

    public static MemoryDiagnosticsService? MemoryDiagnosticsService { get; set; }

    public static DiagnosticsWindow? DiagnosticsWindow { get; set; }

    public static void Reset()
    {
        Config = null!;
        CommandHandler = null;
        DtrService = null;
        MemoryDiagnosticsService = null;
        DiagnosticsWindow = null;
    }
}
