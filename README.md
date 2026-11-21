# ZeffLens

Dalamud diagnostics for plugin developers. Open with `/zefflens`.

- **Performance** compares frame pacing and profiles managed methods or allocations.
- **Memory** tracks commit, RAM, and managed heap use.
- **Atk Addons** times lifecycle callbacks and counts UI nodes.
- **Reloads** compares old and new loads and keeps a record of each run.

Each capture exports its own JSON. Use **Memory → Export full report** or `/zefflens export` for the whole report.

Build with `dotnet build ZeffLens.sln -c Release`. The profiler uses the x64 .NET 10 runtime. Keep `profiler/` beside `ZeffLens.dll`.
