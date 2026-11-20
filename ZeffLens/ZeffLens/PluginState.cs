using ZeffLens.Addons;
using ZeffLens.Configuration;
using ZeffLens.Commands;

namespace ZeffLens;

public static class PluginState
{
    public static SystemConfiguration Config { get; set; } = null!;





    public static ConfigAddon ConfigAddon { get; set; } = null!;

    public static CommandHandler? CommandHandler { get; set; }

    public static DtrService? DtrService { get; set; }


    public static void Reset()
    {
        Config = null!;
        ConfigAddon = null!;
        CommandHandler = null;
        DtrService = null;
    }
}

