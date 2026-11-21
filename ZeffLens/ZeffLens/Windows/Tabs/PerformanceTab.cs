using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;

namespace ZeffLens.Windows;

internal sealed class PerformanceTab(MemoryDiagnosticsService diagnostics)
{
    private readonly PluginProfilerTab _profiler = new(diagnostics);
    private readonly FramePacingTab _framePacing = new(diagnostics);

    public void Draw()
    {
        using var tabs = ImRaii.TabBar("##PerformanceViews");
        if (!tabs)
            return;

        using (var tab = ImRaii.TabItem("Scan plugins"))
        {
            if (tab)
                _profiler.DrawScan();
        }

        using (var tab = ImRaii.TabItem("Frame pacing"))
        {
            if (tab)
                _framePacing.Draw();
        }

        var flags = _profiler.FocusRequested ? ImGuiTabItemFlags.SetSelected : ImGuiTabItemFlags.None;
        _profiler.FocusRequested = false;
        using var profiler = ImRaii.TabItem("Plugin profiler", flags);
        if (profiler)
            _profiler.Draw();
    }
}
