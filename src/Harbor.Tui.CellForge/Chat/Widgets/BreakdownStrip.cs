using Ensure = Harbor.Abstractions.Extensions.Ensure;

namespace Harbor.Tui.CellForge.Widgets;

/// <summary>
/// One slice of a <see cref="BreakdownStrip" />: a trimmed label plus a
/// non-negative weight. Zero-weight slices occupy no cells but keep their
/// tag row so the legend stays stable.
/// </summary>
public readonly record struct BreakdownSegment(string Label, double Value);

/// <summary>
/// Cell-native stacked 0–100% strip (Spectre <c>BreakdownChart</c> pattern):
/// a single-row run partitioned across segments proportionally to their
/// weights, filling the rect width exactly via largest-remainder
/// rounding. The per-segment fill uses <see cref="GaugeBar.Filled" /> so the
/// strip reads as one bar; per-segment styles default to a five-way palette
/// cycle (accent/success/warning/error/tool) when the host passes none.
/// Zero total (or no segments) paints the <see cref="GaugeBar.Track" /> run.
/// Host-agnostic: token-breakdown and cost-share rows adopt this for the
/// compact strip; the legend/tags stay host-owned via <see cref="TagText" />.
/// Paints nothing when the rect is degenerate. Allocation-free on paint
/// (counts computed into a caller-sized <see cref="Span{T}" />).
/// </summary>
public static class BreakdownStrip
{
    private const int MaxStackSegments = 32;

    /// <summary>Non-negative finite weight (NaN/negative → 0).</summary>
    public static double Sanitize(double v) =>
        double.IsNaN(v) || double.IsNegativeInfinity(v) ? 0
        : double.IsPositiveInfinity(v) ? double.MaxValue / 2
        : Math.Max(0, v);

    /// <summary>Sum of sanitized weights.</summary>
    public static double Total(IReadOnlyList<BreakdownSegment> segments)
    {
        double total = 0;
        for (int i = 0; i < segments.Count; i++)
        {
            total += Sanitize(segments[i].Value);
        }

        return total;
    }

    /// <summary>
    /// Cell counts per segment summing to <paramref name="width" /> exactly
    /// (largest-remainder on fractional parts; ties break left-to-right).
    /// Zero-total spreads nothing — every count is 0.
    /// </summary>
    public static void CellCounts(
        IReadOnlyList<BreakdownSegment> segments,
        int width,
        Span<int> counts)
    {
        Ensure.NotNull(segments);
        if (counts.Length < segments.Count)
        {
            throw new ArgumentException("Counts span too small.", nameof(counts));
        }

        for (int i = 0; i < segments.Count; i++)
        {
            counts[i] = 0;
        }

        if (width <= 0 || segments.Count == 0)
        {
            return;
        }

        double total = Total(segments);
        if (total <= 0)
        {
            return;
        }

        int assigned = 0;
        Span<double> remainders = segments.Count <= MaxStackSegments
            ? stackalloc double[segments.Count]
            : new double[segments.Count];
        for (int i = 0; i < segments.Count; i++)
        {
            double exact = (Sanitize(segments[i].Value) / total) * width;
            int whole = (int)Math.Floor(exact);
            counts[i] = whole;
            remainders[i] = exact - whole;
            assigned += whole;
        }

        int left = width - assigned;
        while (left > 0)
        {
            int best = -1;
            for (int i = 0; i < segments.Count; i++)
            {
                if (remainders[i] > 0 && (best < 0 || remainders[i] > remainders[best]))
                {
                    best = i;
                }
            }

            if (best < 0)
            {
                break;
            }

            counts[best]++;
            remainders[best] = 0;
            left--;
        }
    }

    /// <summary>
    /// Paints the strip into the first row of <paramref name="rect" />.
    /// <paramref name="styles" /> overrides per-segment fills; shorter (or
    /// null) falls back to the palette cycle for the tail.
    /// </summary>
    public static void Paint(
        ScreenBuffer buffer,
        Rect rect,
        IReadOnlyList<BreakdownSegment> segments,
        IReadOnlyList<CellStyle>? styles = null)
    {
        Ensure.NotNull(buffer);
        Ensure.NotNull(segments);
        if (rect.Width <= 0 || rect.Height <= 0)
        {
            return;
        }

        int y = rect.Y;
        int x0 = rect.X;
        int width = rect.Width;

        if (segments.Count == 0 || Total(segments) <= 0)
        {
            buffer.SetText(x0, y, new string(GaugeBar.Track, width), ChatPalette.Dim);
            return;
        }

        Span<int> counts = segments.Count <= MaxStackSegments
            ? stackalloc int[segments.Count]
            : new int[segments.Count];
        CellCounts(segments, width, counts);

        int cursor = x0;
        for (int i = 0; i < segments.Count && cursor < x0 + width; i++)
        {
            int cells = Math.Min(counts[i], (x0 + width) - cursor);
            if (cells <= 0)
            {
                continue;
            }

            CellStyle style = ResolveStyle(i, styles);
            buffer.SetText(cursor, y, new string(GaugeBar.Filled, cells), style);
            cursor += cells;
        }
    }

    /// <summary>
    /// Legend line for the strip: <c>"label 42% · label 58%"</c> with shares
    /// of the sanitized total (one decimal dropped when integral). Pure —
    /// the host paints it wherever tags belong.
    /// </summary>
    public static string TagText(IReadOnlyList<BreakdownSegment> segments)
    {
        Ensure.NotNull(segments);
        if (segments.Count == 0)
        {
            return "(empty)";
        }

        double total = Total(segments);
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < segments.Count; i++)
        {
            if (i > 0)
            {
                sb.Append(" · ");
            }

            string label = string.IsNullOrWhiteSpace(segments[i].Label)
                ? "?"
                : segments[i].Label.Trim();
            sb.Append(label).Append(' ');
            double pct = total <= 0 ? 0 : (Sanitize(segments[i].Value) / total) * 100;
            sb.Append(pct.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture));
            sb.Append('%');
        }

        return sb.ToString();
    }

    private static CellStyle ResolveStyle(int index, IReadOnlyList<CellStyle>? styles)
    {
        if (styles is not null && index < styles.Count)
        {
            return styles[index];
        }

        return (index % 5) switch
        {
            0 => new CellStyle(ChatPalette.Accent),
            1 => new CellStyle(ChatPalette.Success),
            2 => new CellStyle(ChatPalette.Warning),
            3 => new CellStyle(ChatPalette.Error),
            _ => new CellStyle(ChatPalette.Tool),
        };
    }
}
