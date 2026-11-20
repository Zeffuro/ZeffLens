using System;
using Dalamud.Game.Gui.Dtr;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Plugin.Services;

namespace ZeffLens;

public sealed class DtrService : IDisposable
{
    private const string EntryTitle = "ZeffLens";

    private IDtrBarEntry? dtrEntry;
    private DateTime lastUpdate = DateTime.MinValue;

    public DtrService()
    {
        Service.Framework.Update += this.OnFrameworkUpdate;
    }

    public void Dispose()
    {
        Service.Framework.Update -= this.OnFrameworkUpdate;
        this.RemoveEntry();
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        if ((DateTime.Now - this.lastUpdate).TotalMilliseconds < 500)
        {
            return;
        }

        this.lastUpdate = DateTime.Now;
        this.UpdateBar();
    }

    private void UpdateBar()
    {
        var settings = PluginState.Config.Dtr;
        if (!settings.Enabled)
        {
            this.RemoveEntry();
            return;
        }

        this.EnsureEntry();
        if (this.dtrEntry is null)
        {
            return;
        }

        if (!Service.ClientState.IsLoggedIn && !settings.ShowWhenLoggedOut)
        {
            this.dtrEntry.Shown = false;
            return;
        }

        this.dtrEntry.Text = new SeStringBuilder().AddText(settings.Text).Build();
        this.dtrEntry.Tooltip = new SeStringBuilder().AddText(settings.Tooltip).Build();
        this.dtrEntry.Shown = true;
    }

    private void EnsureEntry()
    {
        if (this.dtrEntry is not null)
        {
            return;
        }

        this.dtrEntry = Service.DtrBar.Get(EntryTitle);
        this.dtrEntry.Shown = true;
        this.dtrEntry.OnClick += OnDtrClick;
    }

    private void RemoveEntry()
    {
        if (this.dtrEntry is null)
        {
            return;
        }

        this.dtrEntry.OnClick -= OnDtrClick;
        this.dtrEntry.Remove();
        this.dtrEntry = null;
    }

    private static void OnDtrClick(DtrInteractionEvent interaction)
    {
        if (PluginState.Config.Dtr.ClickToOpenConfig)
        {
            PluginState.ConfigAddon.Toggle();
            return;
        }

    }
}

