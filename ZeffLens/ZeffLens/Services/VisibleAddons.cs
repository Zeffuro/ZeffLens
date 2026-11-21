using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI;
using ZeffLens.Diagnostics;

namespace ZeffLens;

internal sealed class VisibleAddons(IFramework framework, IPluginLog log)
{
    private volatile string[] _names = [];
    private volatile string? _error;
    private int _pending;

    public (string[] Names, bool Pending, string? Error) Read()
        => (_names, Volatile.Read(ref _pending) != 0, _error);

    public void Refresh()
    {
        if (Interlocked.Exchange(ref _pending, 1) == 0)
            _ = RefreshAsync();
    }

    public static unsafe AddonNativeSnapshot? CaptureProfile(IFramework framework, string exactName)
    {
        if (!framework.IsInFrameworkUpdateThread || framework.IsFrameworkUnloading)
            return null;

        var manager = RaptureAtkUnitManager.Instance();
        if (manager is null)
            return null;

        var list = &manager->AtkUnitManager.AllLoadedUnitsList;
        var entries = list->Entries;
        var count = list->Count;
        if (count > entries.Length)
            return null;

        var loaded = 0;
        var visible = 0;
        var nodes = 0;
        for (var i = 0; i < count; i++)
        {
            var unit = entries[i].Value;
            if (unit is null || !string.Equals(unit->NameString, exactName, StringComparison.Ordinal))
                continue;

            loaded++;
            if (unit->IsVisible)
                visible++;
            nodes += unit->UldManager.NodeListCount;
        }

        return new AddonNativeSnapshot(loaded, visible, nodes);
    }

    private async Task RefreshAsync()
    {
        try
        {
            _error = null;
            _names = await framework.RunOnFrameworkThread(Snapshot).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _error = "Addon list unavailable.";
        }
        catch (Exception ex)
        {
            log.Warning(ex, "Could not list visible addons.");
            _error = "Could not list visible addons. See the Dalamud log.";
        }
        finally
        {
            Volatile.Write(ref _pending, 0);
        }
    }

    private static unsafe string[] Snapshot()
    {
        var manager = RaptureAtkUnitManager.Instance();
        if (manager is null)
            return [];

        var list = &manager->AtkUnitManager.AllLoadedUnitsList;
        var entries = list->Entries;
        var names = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < Math.Min(list->Count, entries.Length); i++)
        {
            var unit = entries[i].Value;
            if (unit is null || !unit->IsReady || !unit->IsVisible)
                continue;

            var name = unit->NameString;
            if (AddonWatch.IsValidName(name))
                names.Add(name);
        }

        return names.Order(StringComparer.OrdinalIgnoreCase).ToArray();
    }
}
