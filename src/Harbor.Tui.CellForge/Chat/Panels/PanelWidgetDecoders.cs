using Harbor.Ui.Framework.Panels;

namespace Harbor.Tui.CellForge.Panels;

/// <summary>
///     Priority-ordered widget decoder: tries one widget shape and flattens it
///     to CellForge text rows. Shaped after the RazorConsole translator sample
///     (30+ <c>IVdomElementTranslator</c>, priority-ordered, first match wins),
///     except the output is plain rows/cells — never a Spectre widget.
/// </summary>
/// <remarks>
///     Higher <see cref="Priority" /> runs first. Decoders run before the three
///     legacy <c>WidgetToRows</c> shapes (string / string-list / ToString
///     fallback), so a panel can return a richer widget (progress / table /
///     tree / form below) without breaking the text-rows contract.
/// </remarks>
public interface IPanelWidgetDecoder
{
    /// <summary>Higher wins; sample decoders below use 100.</summary>
    public int Priority { get; }

    /// <summary>
    ///     Try to flatten <paramref name="widget" /> to rows.
    ///     Return <see langword="false" /> (leaving <paramref name="rows" />
    ///     <see langword="null" />) when this decoder does not handle the shape.
    /// </summary>
    public bool TryDecode(object widget, PanelContext ctx, out IReadOnlyList<string>? rows);
}

/// <summary>Labeled fractional progress (0..1, clamped) → one bar row.</summary>
public sealed record PanelProgress(string Label, double Fraction);

/// <summary>Header + data rows → <c>a | b</c> rows with a separator line.</summary>
public sealed record PanelTable(IReadOnlyList<string> Headers, IReadOnlyList<IReadOnlyList<string>> Rows);

/// <summary>Depth-indented nodes → one row per node.</summary>
public sealed record PanelTree(IReadOnlyList<(int Depth, string Label)> Nodes);

/// <summary>Titled form → title row plus <c>name: value</c> rows.</summary>
public sealed record PanelForm(string Title, IReadOnlyList<(string Name, string Value)> Fields);

/// <summary>Sample decoders for the four richer widgets (all priority 100).</summary>
public static class PanelWidgetDecoders
{
    public sealed class Progress : IPanelWidgetDecoder
    {
        public int Priority => 100;

        public bool TryDecode(object widget, PanelContext ctx, out IReadOnlyList<string>? rows)
        {
            if (widget is not PanelProgress p)
            {
                rows = null;
                return false;
            }

            double f = Math.Clamp(p.Fraction, 0, 1);
            int filled = (int)Math.Round(f * 16);
            rows = [$"{p.Label} [{new string('#', filled)}{new string('-', 16 - filled)}] {(int)Math.Round(f * 100)}%"];
            return true;
        }
    }

    public sealed class Table : IPanelWidgetDecoder
    {
        public int Priority => 100;

        public bool TryDecode(object widget, PanelContext ctx, out IReadOnlyList<string>? rows)
        {
            if (widget is not PanelTable t)
            {
                rows = null;
                return false;
            }

            var list = new List<string>(t.Rows.Count + 2) { string.Join(" | ", t.Headers) };
            list.Add(new string('-', Math.Max(list[0].Length, 3)));
            foreach (var r in t.Rows)
            {
                list.Add(string.Join(" | ", r));
            }

            rows = list;
            return true;
        }
    }

    public sealed class Tree : IPanelWidgetDecoder
    {
        public int Priority => 100;

        public bool TryDecode(object widget, PanelContext ctx, out IReadOnlyList<string>? rows)
        {
            if (widget is not PanelTree t)
            {
                rows = null;
                return false;
            }

            rows = t.Nodes.Select(n => $"{new string(' ', Math.Max(n.Depth, 0) * 2)}{n.Label}").ToArray();
            return true;
        }
    }

    public sealed class Form : IPanelWidgetDecoder
    {
        public int Priority => 100;

        public bool TryDecode(object widget, PanelContext ctx, out IReadOnlyList<string>? rows)
        {
            if (widget is not PanelForm f)
            {
                rows = null;
                return false;
            }

            var list = new List<string>(f.Fields.Count + 1) { f.Title };
            foreach (var field in f.Fields)
            {
                list.Add($"{field.Name}: {field.Value}");
            }

            rows = list;
            return true;
        }
    }
}
