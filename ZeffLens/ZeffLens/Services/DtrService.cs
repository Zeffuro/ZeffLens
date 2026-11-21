using System;
using System.Diagnostics;
using Dalamud.Game.Gui.Dtr;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Plugin.Services;

namespace ZeffLens;

public sealed class DtrService : IDisposable
{
    private const string EntryTitle = "ZeffLens";

    private IDtrBarEntry? _dtrEntry;
    private long _lastUpdate;
    private string? _text;
    private string? _tooltip;

    public DtrService()
    {
        Service.Framework.Update += OnFrameworkUpdate;
    }

    public void Dispose()
    {
        Service.Framework.Update -= OnFrameworkUpdate;
        RemoveEntry();
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        if (!PluginState.Config.Dtr.Enabled && _dtrEntry is null)
            return;

        var now = Stopwatch.GetTimestamp();
        if (Stopwatch.GetElapsedTime(_lastUpdate, now).TotalMilliseconds < 500)
        {
            return;
        }

        _lastUpdate = now;
        UpdateBar();
    }

    private void UpdateBar()
    {
        var settings = PluginState.Config.Dtr;
        if (!settings.Enabled)
        {
            RemoveEntry();
            return;
        }

        EnsureEntry();
        if (_dtrEntry is null)
        {
            return;
        }

        if (!Service.ClientState.IsLoggedIn && !settings.ShowWhenLoggedOut)
        {
            _dtrEntry.Shown = false;
            return;
        }

        if (_text != settings.Text)
        {
            _text = settings.Text;
            _dtrEntry.Text = new SeStringBuilder().AddText(_text).Build();
        }

        if (_tooltip != settings.Tooltip)
        {
            _tooltip = settings.Tooltip;
            _dtrEntry.Tooltip = new SeStringBuilder().AddText(_tooltip).Build();
        }
        _dtrEntry.Shown = true;
    }

    private void EnsureEntry()
    {
        if (_dtrEntry is not null)
        {
            return;
        }

        _dtrEntry = Service.DtrBar.Get(EntryTitle);
        _dtrEntry.Shown = true;
        _dtrEntry.OnClick += OnDtrClick;
    }

    private void RemoveEntry()
    {
        if (_dtrEntry is null)
        {
            return;
        }

        _dtrEntry.OnClick -= OnDtrClick;
        _dtrEntry.Remove();
        _dtrEntry = null;
        _text = null;
        _tooltip = null;
    }

    private static void OnDtrClick(DtrInteractionEvent interaction)
    {
        if (PluginState.Config.Dtr.ClickToOpenConfig)
        {
            PluginState.DiagnosticsWindow?.Toggle();
            return;
        }
    }
}
