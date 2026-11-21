using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace ZeffLens;

public sealed class Service
{
    [PluginService] public static IDalamudPluginInterface PluginInterface { get; private set; } = null!;

    [PluginService] public static IPluginLog Logger { get; private set; } = null!;

    [PluginService] public static ICommandManager CommandManager { get; private set; } = null!;

    [PluginService] public static IChatGui ChatGui { get; private set; } = null!;
    [PluginService] public static INotificationManager NotificationManager { get; private set; } = null!;

    [PluginService] public static IFramework Framework { get; private set; } = null!;
    [PluginService] public static IAddonLifecycle AddonLifecycle { get; private set; } = null!;

    [PluginService] public static IDtrBar DtrBar { get; private set; } = null!;

    [PluginService] public static IClientState ClientState { get; private set; } = null!;

    [PluginService] public static IGameInteropProvider GameInteropProvider { get; private set; } = null!;

    public static void Reset()
    {
        PluginInterface = null!;
        Logger = null!;
        CommandManager = null!;
        ChatGui = null!;
        NotificationManager = null!;
        Framework = null!;
        AddonLifecycle = null!;
        DtrBar = null!;
        ClientState = null!;
        GameInteropProvider = null!;
    }
}
