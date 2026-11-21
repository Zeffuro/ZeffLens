using System;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using ZeffLens.Configuration.Persistence;
using ZeffLens.Commands;
using ZeffLens.Windows;

namespace ZeffLens;

public sealed class Plugin : IAsyncDalamudPlugin
{
    private readonly WindowSystem _windows = new();

    public Plugin(IDalamudPluginInterface pluginInterface)
    {
        pluginInterface.Create<Service>();
        PluginState.Reset();
    }

    public Task LoadAsync(CancellationToken cancellationToken)
    {
        PluginState.Config = ConfigRepository.LoadOrDefault();
        ConfigBackup.DoConfigBackup(Service.PluginInterface);

        PluginState.MemoryDiagnosticsService = new MemoryDiagnosticsService();
        PluginState.DiagnosticsWindow = new DiagnosticsWindow(PluginState.MemoryDiagnosticsService);
        _windows.AddWindow(PluginState.DiagnosticsWindow);

        Service.PluginInterface.UiBuilder.Draw += _windows.Draw;
        Service.PluginInterface.UiBuilder.OpenMainUi += ToggleDiagnosticsUi;
        Service.PluginInterface.UiBuilder.OpenConfigUi += ToggleDiagnosticsUi;

        PluginState.CommandHandler = new CommandHandler();
        PluginState.DtrService = new DtrService();

        ConfigRepository.SaveImmediate(PluginState.Config);
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        var config = PluginState.Config;
        var commandHandler = PluginState.CommandHandler;
        var dtrService = PluginState.DtrService;
        var memoryDiagnosticsService = PluginState.MemoryDiagnosticsService;
        var logger = Service.Logger;

        Service.PluginInterface.UiBuilder.Draw -= _windows.Draw;
        Service.PluginInterface.UiBuilder.OpenMainUi -= ToggleDiagnosticsUi;
        Service.PluginInterface.UiBuilder.OpenConfigUi -= ToggleDiagnosticsUi;
        _windows.RemoveAllWindows();

        DisposeSafely(commandHandler, nameof(CommandHandler), logger);
        DisposeSafely(dtrService, nameof(DtrService), logger);
        if (memoryDiagnosticsService is not null)
            await memoryDiagnosticsService.ManagedProfiles.ShutdownAsync().ConfigureAwait(false);
        DisposeSafely(memoryDiagnosticsService, nameof(MemoryDiagnosticsService), logger);

        try
        {
            ConfigRepository.SaveImmediate(config);
        }
        catch (Exception ex)
        {
            logger.Warning(ex, "Failed to save configuration during disposal.");
        }
        finally
        {
            PluginState.Reset();
            Service.Reset();
        }

    }

    private static void ToggleDiagnosticsUi()
    {
        PluginState.DiagnosticsWindow?.Toggle();
    }

    private static void DisposeSafely(IDisposable? disposable, string name, IPluginLog logger)
    {
        if (disposable is null)
        {
            return;
        }

        try
        {
            disposable.Dispose();
        }
        catch (Exception ex)
        {
            logger.Warning(ex, "Failed to dispose {ServiceName}.", name);
        }
    }
}
