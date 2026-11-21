# ZeffLens

Dalamud diagnostics for plugin developers. Open with `/zefflens`.

- **Performance** compares frame pacing and profiles managed methods or allocations.
- **Memory** tracks commit, RAM, and managed heap use.
- **Atk Addons** times lifecycle callbacks and counts UI nodes.
- **Reloads** compares old and new loads and keeps a record of each run.

Each capture exports its own JSON. Use **Memory → Export full report** or `/zefflens export` for the whole report.

## Installation

1. Run `/xlsettings` in-game and open the **Settings** tab.
2. Add `https://raw.githubusercontent.com/Zeffuro/MyDalamudPlugins/main/repo.json` under **Custom Plugin Repositories**. If you already use NativeMeters from this feed, skip this step.
3. Open the **Plugin Installer** and install **ZeffLens**.

## Development

Build with `dotnet build ZeffLens.sln -c Release`. The profiler uses the x64 .NET 10 runtime. Keep `profiler/` beside `ZeffLens.dll`.
