using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;

namespace ZeffLens.Windows;

public sealed class DiagnosticsWindow : Window
{
    private readonly PerformanceTab _performance;
    private readonly MemoryTab _memory;
    private readonly AddonTab _addons;
    private readonly ReloadsTab _reloads;
    private readonly ContextsTab _contexts;
    private readonly ResourcesTab _resources;
    private readonly SettingsTab _settings;

    public DiagnosticsWindow(MemoryDiagnosticsService diagnostics) : base("ZeffLens###ZeffLensDiagnostics")
    {
        var filter = new PluginFilter();
        _performance = new PerformanceTab(diagnostics);
        _memory = new MemoryTab(diagnostics);
        _addons = new AddonTab(diagnostics);
        _reloads = new ReloadsTab(diagnostics, filter);
        _contexts = new ContextsTab(diagnostics, filter);
        _resources = new ResourcesTab(diagnostics, filter);
        _settings = new SettingsTab(diagnostics);
        Size = new Vector2(980, 680);
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    internal void OpenReloadComparison()
    {
        _reloads.FocusRequested = true;
        IsOpen = true;
    }

    public override void Draw()
    {
        using var tabs = ImRaii.TabBar("##ZeffLensDiagnosticsTabs");
        if (!tabs)
            return;

        using (var tab = ImRaii.TabItem("Performance"))
        {
            if (tab)
                _performance.Draw();
        }

        using (var tab = ImRaii.TabItem("Memory"))
        {
            if (tab)
                _memory.Draw();
        }

        using (var tab = ImRaii.TabItem("Atk Addons"))
        {
            if (tab)
                _addons.Draw();
        }

        var reloadFlags = _reloads.FocusRequested ? ImGuiTabItemFlags.SetSelected : ImGuiTabItemFlags.None;
        using (var tab = ImRaii.TabItem("Reloads", reloadFlags))
        {
            if (tab)
                _reloads.Draw();
        }

        using (var tab = ImRaii.TabItem("Contexts"))
        {
            if (tab)
                _contexts.Draw();
        }

        using (var tab = ImRaii.TabItem("Resources"))
        {
            if (tab)
                _resources.Draw();
        }

        using (var tab = ImRaii.TabItem("Settings"))
        {
            if (tab)
                _settings.Draw();
        }
    }
}
