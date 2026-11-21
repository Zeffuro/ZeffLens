using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using ZeffLens.Diagnostics;

namespace ZeffLens.Windows;

internal static class DiagnosticsWidgets
{
    internal static readonly Vector4 PositiveDeltaColor = new(1.0f, 0.45f, 0.35f, 1.0f);
    internal static readonly Vector4 NegativeDeltaColor = new(0.45f, 0.85f, 0.45f, 1.0f);
    internal static readonly Vector4 NeutralColor = new(0.72f, 0.72f, 0.72f, 1.0f);
    internal static readonly Vector4 WarningColor = new(1.0f, 0.78f, 0.30f, 1.0f);
    internal static readonly Vector4 ErrorColor = new(1.0f, 0.35f, 0.35f, 1.0f);

    private static readonly IComparer<IComparable?> SortValueComparer = Comparer<IComparable?>.Create(CompareSortValues);

    internal sealed class RowView<T>
    {
        private T[]? _source;
        private string? _filter;
        private T[] _rows = [];
        private int _column = -1;
        private ImGuiSortDirection _direction;

        public T[] Filter(T[] source, string filter, Func<T, bool> matches, bool newestFirst = false)
        {
            if (ReferenceEquals(_source, source) && _filter == filter)
                return _rows;

            _source = source;
            _filter = filter;
            _rows = source.Where(matches).ToArray();
            if (newestFirst)
                Array.Reverse(_rows);
            _column = -1;
            return _rows;
        }

        public T[] Sort(Func<T, int, IComparable?> keySelector)
        {
            var sortSpecs = ImGui.TableGetSortSpecs();
            if (sortSpecs.IsNull || sortSpecs.SpecsCount == 0)
                return _rows;

            var spec = sortSpecs.Specs;
            if (spec.SortDirection == ImGuiSortDirection.None)
                return _rows;

            if (_column == spec.ColumnIndex && _direction == spec.SortDirection)
                return _rows;

            _column = spec.ColumnIndex;
            _direction = spec.SortDirection;
            _rows = _direction == ImGuiSortDirection.Descending
                ? _rows.OrderByDescending(row => keySelector(row, _column), SortValueComparer).ToArray()
                : _rows.OrderBy(row => keySelector(row, _column), SortValueComparer).ToArray();
            sortSpecs.SpecsDirty = false;
            return _rows;
        }
    }

    internal static int GetContextStatusRank(PluginContextSnapshot context)
    {
        if (context.SuspectedRetainedAfterUnload)
        {
            return 3;
        }

        if (context.IsRetired && context.AssemblyLoadContextAlive)
        {
            return 2;
        }

        return context.IsRetired ? 0 : 1;
    }

    internal static int GetProbeStateRank(PluginResourceProbeSnapshot probe)
    {
        if (probe.IsRetiredContext)
        {
            return 2;
        }

        return probe.IsLoaded ? 1 : 0;
    }

    private static int CompareSortValues(IComparable? left, IComparable? right)
    {
        if (ReferenceEquals(left, right))
        {
            return 0;
        }

        if (left is null)
        {
            return -1;
        }

        if (right is null)
        {
            return 1;
        }

        return left.CompareTo(right);
    }

    internal static ImGuiTableFlags SortableTableFlags
        => ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.Sortable;

    internal static ImGuiTableFlags ScrollableSortableTableFlags
        => SortableTableFlags | ImGuiTableFlags.ScrollY;

    internal static void DrawClippedRows<T>(T[] rows, Action<T> draw)
    {
        var clipper = ImGui.ImGuiListClipper();
        try
        {
            clipper.Begin(rows.Length);
            while (clipper.Step())
            {
                for (var index = clipper.DisplayStart; index < clipper.DisplayEnd; index++)
                    draw(rows[index]);
            }
        }
        finally
        {
            clipper.Destroy();
        }
    }

    internal static string FormatTime(DateTime utc)
    {
        return utc.ToLocalTime().ToString("HH:mm:ss");
    }

    internal static void DrawTableCell(string text)
    {
        ImGui.TableNextColumn();
        ImGui.TextUnformatted(text);
    }

    internal static bool ExportButton(string label)
    {
        using var disabled = ImRaii.Disabled(ReportExport.IsWriting);
        return ImGui.Button(ReportExport.IsWriting ? "Exporting…" : label);
    }

    internal static void DrawLoadedPluginPicker(MemoryDiagnosticsService diagnostics, string id, ref string? selection, ref string filter, bool includeZeffLens = false)
    {
        using var scopeId = ImRaii.PushId(id);
        ImGui.SetNextItemWidth(290);
        using (var combo = ImRaii.Combo("Plugin", selection ?? "Choose a loaded plugin"))
        {
            if (combo)
            {
                ImGui.InputTextWithHint("##PluginSearch", "Find plugin", ref filter, 128);
                var ownName = Service.PluginInterface.InternalName;
                foreach (var context in diagnostics.GetPluginContexts())
                {
                    if (!context.IsLoaded || context.IsRetired || (!includeZeffLens && context.InternalName == ownName) ||
                        !(context.InternalName.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                          context.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)))
                        continue;

                    var label = context.Name == context.InternalName ? context.InternalName : $"{context.Name} ({context.InternalName})";
                    if (ImGui.Selectable(label, selection == context.InternalName))
                        selection = context.InternalName;
                }
            }
        }
    }

    internal static void DrawDeltaOnly(long? value, long? previousValue, bool colorDeltas = true)
    {
        if (value is null || previousValue is null)
        {
            ImGui.TextDisabled("—");
            return;
        }

        var delta = value.Value - previousValue.Value;
        if (!colorDeltas)
        {
            ImGui.TextUnformatted(DiagnosticFormatting.FormatSignedBytes(delta));
            return;
        }
        var color = delta > 0 ? PositiveDeltaColor : delta < 0 ? NegativeDeltaColor : NeutralColor;
        ImGui.TextColored(color, DiagnosticFormatting.FormatSignedBytes(delta));
    }
}
