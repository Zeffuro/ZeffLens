using System.Threading;
using System.Threading.Tasks;
using Dalamud.Plugin;
using ZeffLens.Configuration;
using ZeffLens.Configuration.Persistence;
using ZeffLens.Commands;
using KamiToolKit;
using KamiToolKit.BaseTypes;
using ZeffLens.Addons;

namespace ZeffLens;

public sealed class Plugin : IAsyncDalamudPlugin
{
    public Plugin(IDalamudPluginInterface pluginInterface)
    {
        pluginInterface.Create<Service>();
        PluginState.Reset();
    }

    public Task LoadAsync(CancellationToken cancellationToken)
    {
        PluginState.Config = ConfigRepository.LoadOrDefault();
        ConfigBackup.DoConfigBackup(Service.PluginInterface);


        KamiToolKitLibrary.Initialize(Service.PluginInterface, "ZeffLens");


        PluginState.ConfigAddon = new ConfigAddon
        {
            InternalName = "ZeffLens_Config",
            Title = "ZeffLens Config",
            Size = new(600.0f, 430.0f),
        };

        Service.PluginInterface.UiBuilder.OpenMainUi += ToggleConfigUi;
        Service.PluginInterface.UiBuilder.OpenConfigUi += ToggleConfigUi;

        PluginState.CommandHandler = new CommandHandler();

        PluginState.DtrService = new DtrService();



        ConfigRepository.SaveImmediate(PluginState.Config);
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        var isFrameworkUnloading = Service.Framework.IsFrameworkUnloading;

        PluginState.CommandHandler?.Dispose();
        PluginState.DtrService?.Dispose();


        Service.PluginInterface.UiBuilder.OpenMainUi -= ToggleConfigUi;
        Service.PluginInterface.UiBuilder.OpenConfigUi -= ToggleConfigUi;

        if (!isFrameworkUnloading)
        {
            await DisposeNativeUiAsync();
        }

        await Service.Framework.RunOnFrameworkThread(KamiToolKitLibrary.Dispose);


        ConfigRepository.SaveImmediate(PluginState.Config);

        if (!isFrameworkUnloading)
        {
            PluginState.Reset();
        }

    }


    private static async ValueTask DisposeNativeUiAsync()
    {
        await DisposeNativeAddonAsync(PluginState.ConfigAddon);
        PluginState.ConfigAddon = null!;


    }

    private static async ValueTask DisposeNativeAddonAsync(NativeAddon? addon)
    {
        if (addon is null)
        {
            return;
        }

        if (Service.Framework.IsInFrameworkUpdateThread)
        {
            addon.Dispose();
            return;
        }

        await addon.DisposeAsync();
    }



    private static void ToggleConfigUi()
    {
        PluginState.ConfigAddon?.Toggle();
    }
}


