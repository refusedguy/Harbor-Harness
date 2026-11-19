// #3087 (S6): the single public entry of the BCL-only widget catalog.
// Every ready widget is a declarative WidgetSpec; WidgetCatalog is the one
// facade that measures, paints and moves its selection. The painter writes
// only into the engine ScreenBuffer through its public cell API, so the
// surface adds no dependency (not even a ProjectReference) to the leaf and
// keeps the AOT analyzers at zero IL2xxx warnings.
//
// The six isolated engine leaves (TileAtlas/TileCamera, FacetedHistoryState,
// TwoPaneState, NavigableChangeBlocks, TerminalTitleWriter) are deliberately
// NOT re-exported here. They are different concerns; wrapping them without a
// named consumer would add a second vocabulary, which this slice forbids.

namespace Harbor.Tui.CellForge.Catalog;

using Harbor.Tui.CellForge.Rendering;

/// <summary>One declarative catalog row: a stable kind, name and one-line purpose.</summary>
public readonly record struct WidgetDefinition(WidgetKind Kind, string Name, string Description);

/// <summary>
/// BCL-only catalog of five ready widgets. All painting goes through
/// <see cref="Paint"/>, all sizing through <see cref="Measure"/>; there is no
/// second entry point and no per-frame allocation in a paint path.
/// </summary>
public static class WidgetCatalog
{
    /// <summary>The declarative inventory, in paint order. Five entries, one per kind.</summary>
    public static IReadOnlyList<WidgetDefinition> Widgets { get; } =
    [
        new(WidgetKind.Paragraph, "paragraph", "Text lines clipped to the area"),
        new(WidgetKind.Rule, "rule", "One horizontal row of a glyph"),
        new(WidgetKind.Gauge, "gauge", "Label plus a proportional bar"),
        new(WidgetKind.List, "list", "Selectable rows with a marker"),
        new(WidgetKind.Status, "status", "Caption with a right-aligned value"),
    ];

    /// <summary>
    /// Desired size of <paramref name="spec"/> within <paramref name="bounds"/>.
    /// Pure: no buffer writes and no state mutation. Both extents are hard
    /// upper bounds, matching <see cref="Constraints"/>.
    /// </summary>
    public static Size Measure(WidgetSpec spec, Constraints bounds)
    {
        ArgumentNullException.ThrowIfNull(spec);
        int w = bounds.Width;
        int h = bounds.Height;
        return spec switch
        {
            ParagraphSpec p => new Size(
                Math.Min(w, LongestLine(p.Text)),
                p.Text.Length == 0 ? 0 : Math.Min(h, CountLines(p.Text))),
            RuleSpec => new Size(w, Math.Min(h, 1)),
            GaugeSpec => new Size(w, Math.Min(h, 1)),
            ListSpec l => new Size(
                Math.Min(w, 2 + LongestItem(l.Items)),
                Math.Min(h, l.Items.Count)),
            StatusSpec => new Size(w, Math.Min(h, 1)),
            _ => default,
        };
    }

    /// <summary>
    /// The one paint entry. Writes only inside the intersection of
    /// <paramref name="area"/> and the buffer; a zero-sized or off-screen area
    /// is a no-op. Widget state lives in the spec, never in this class.
    /// </summary>
    public static void Paint(WidgetSpec spec, ScreenBuffer buffer, Rect area)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(buffer);

        var rect = area.Intersect(new Rect(0, 0, buffer.Cols, buffer.Rows));
        if (rect.Width <= 0 || rect.Height <= 0)
        {
            return;
        }

        switch (spec)
        {
            case ParagraphSpec paragraph:
                PaintParagraph(buffer, in rect, paragraph);
                break;
            case RuleSpec rule:
                PaintRule(buffer, in rect, rule);
                break;
            case GaugeSpec gauge:
                PaintGauge(buffer, in rect, gauge);
                break;
            case ListSpec list:
                PaintList(buffer, in rect, list);
                break;
            case StatusSpec status:
                PaintStatus(buffer, in rect, status);
                break;
        }
    }

    /// <summary>Clamps an index to a list's valid range; empty lists select -1.</summary>
    public static ListSpec Select(ListSpec spec, int index)
    {
        ArgumentNullException.ThrowIfNull(spec);
        int count = spec.Items.Count;
        return count == 0
            ? spec with { Selected = -1 }
            : spec with { Selected = Math.Clamp(index, 0, count - 1) };
    }

    /// <summary>Moves a list selection by <paramref name="delta"/>, clamped at both ends.</summary>
    public static ListSpec Move(ListSpec spec, int delta)
    {
        ArgumentNullException.ThrowIfNull(spec);
        if (spec.Items.Count == 0)
        {
            return spec with { Selected = -1 };
        }

        int start = spec.Selected < 0 ? 0 : spec.Selected;
        return Select(spec, start + delta);
    }

    private static void PaintParagraph(ScreenBuffer buffer, in Rect rect, ParagraphSpec spec)
    {
        ReadOnlySpan<char> rest = spec.Text.AsSpan();
        int y = rect.Y;
        while (!rest.IsEmpty && y < rect.Bottom)
        {
            int newline = rest.IndexOf('\n');
            ReadOnlySpan<char> line = newline < 0 ? rest : rest[..newline];
            Write(buffer, in rect, rect.X, y, line, spec.Style);
            y++;
            rest = newline < 0 ? ReadOnlySpan<char>.Empty : rest[(newline + 1)..];
        }
    }

    private static void PaintRule(ScreenBuffer buffer, in Rect rect, RuleSpec spec) =>
        buffer.Fill(new Rect(rect.X, rect.Y, rect.Width, 1), Cell.From(spec.Glyph, spec.Style));

    private static void PaintGauge(ScreenBuffer buffer, in Rect rect, GaugeSpec spec)
    {
        int labelLen = Math.Min(spec.Label.Length, rect.Width);
        Write(buffer, in rect, rect.X, rect.Y, spec.Label.AsSpan(0, labelLen), spec.Style);

        int barX = rect.X + labelLen + 1;
        int barWidth = rect.Right - barX;
        if (barWidth <= 0)
        {
            return;
        }

        double ratio = double.IsFinite(spec.Ratio) ? Math.Clamp(spec.Ratio, 0d, 1d) : 0d;
        int filled = (int)(ratio * barWidth);
        for (int i = 0; i < barWidth; i++)
        {
            buffer.SetRune(barX + i, rect.Y, i < filled ? spec.FilledGlyph : spec.EmptyGlyph, spec.Style);
        }
    }

    private static void PaintList(ScreenBuffer buffer, in Rect rect, ListSpec spec)
    {
        int y = rect.Y;
        for (int i = 0; i < spec.Items.Count && y < rect.Bottom; i++, y++)
        {
            string marker = i == spec.Selected ? spec.Marker.ToString() : " ";
            string row = marker + " " + (spec.Items[i] ?? string.Empty);
            Write(buffer, in rect, rect.X, y, row.AsSpan(), spec.Style);
        }
    }

    private static void PaintStatus(ScreenBuffer buffer, in Rect rect, StatusSpec spec)
    {
        int valueLen = Math.Min(spec.Value.Length, rect.Width);
        int captionLen = Math.Min(spec.Caption.Length, Math.Max(0, rect.Width - valueLen - 1));
        Write(buffer, in rect, rect.X, rect.Y, spec.Caption.AsSpan(0, captionLen), spec.Style);
        if (valueLen > 0)
        {
            Write(buffer, in rect, rect.Right - valueLen, rect.Y,
                spec.Value.AsSpan(spec.Value.Length - valueLen), spec.Style);
        }
    }

    private static void Write(ScreenBuffer buffer, in Rect rect, int x, int y, ReadOnlySpan<char> text, in CellStyle style)
    {
        if ((uint)y >= (uint)buffer.Rows || x < rect.X || x >= rect.Right)
        {
            return;
        }

        int max = Math.Min(rect.Right, buffer.Cols) - x;
        if (max <= 0)
        {
            return;
        }

        buffer.SetText(x, y, text[..Math.Min(text.Length, max)], style);
    }

    private static int LongestLine(string text)
    {
        int best = 0;
        int start = 0;
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == '\n')
            {
                best = Math.Max(best, i - start);
                start = i + 1;
            }
        }

        return Math.Max(best, text.Length - start);
    }

    private static int CountLines(string text)
    {
        int lines = 1;
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == '\n')
            {
                lines++;
            }
        }

        return lines;
    }

    private static int LongestItem(IReadOnlyList<string> items)
    {
        int best = 0;
        for (int i = 0; i < items.Count; i++)
        {
            best = Math.Max(best, items[i]?.Length ?? 0);
        }

        return best;
    }
}
