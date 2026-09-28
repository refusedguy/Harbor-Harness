using System.Text;
using Harbor.Ui.Framework.Rendering;

namespace Harbor.Ui.Framework.Rendering.Widgets;

/// <summary>
/// Compact token-rate sparkline ([PRIM10], part of #305): a ratatui
/// <c>sparkline.rs</c> port — one block-glyph per sample
/// (<c>▁▂▃▄▅▆▇█</c>), min..max normalized, clipped to the paint width.
/// Host-agnostic and immutable: the owner feeds per-turn token-rate samples
/// and marks the slot dirty on update. An optional title row labels the panel
/// (e.g. <c>"tok/s"</c>); the sparkline itself is always a single row.
/// </summary>
public sealed class SparklineBlock : IChatBlock
{
    /// <summary>Block glyphs, lowest to highest (ratatui sparkline set).</summary>
    public const string Levels = "▁▂▃▄▅▆▇█";

    /// <summary>Glyph used when every sample is equal (flat line).</summary>
    public const char FlatGlyph = '▄';

    /// <summary>Placeholder painted for an empty series.</summary>
    public const string EmptyText = "(empty)";

    private readonly double[] _values;
    private readonly double? _maxOverride;

    /// <summary>Samples (NaN/±Infinity sanitized to the nearest finite edge).</summary>
    public IReadOnlyList<double> Values => _values;

    /// <summary>Optional title painted dim on the row above the sparkline.</summary>
    public string? Title { get; }

    public SparklineBlock(IEnumerable<double>? values = null, string? title = null, double? maxValue = null)
    {
        if (values is null)
        {
            _values = [];
        }
        else
        {
            var list = new List<double>();
            foreach (var v in values)
            {
                list.Add(Sanitize(v));
            }

            _values = [.. list];
        }

        Title = string.IsNullOrWhiteSpace(title) ? null : title.Trim();
        _maxOverride = maxValue is double m && !double.IsNaN(m) && !double.IsInfinity(m) && m > 0 ? m : null;
    }

    public string Kind => "sparkline";

    public bool IsStreamContinuation => false;

    public int BudgetBytes => 64 + (_values.Length * 8) + ((Title?.Length ?? 0) * 2);

    public BlockMeasure Measure(int width) => BlockMeasure.Exact(Title is null ? 1 : 2);

    public int CheapEstimate(int width) => Title is null ? 1 : 2;

    public void Paint(in BlockPaintContext ctx)
    {
        var buffer = ctx.Buffer;
        int width = ctx.Rect.Width;
        int height = ctx.Rect.Height;
        if (width <= 0 || height <= 0)
        {
            return;
        }

        bool titled = Title is not null;
        int total = titled ? 2 : 1;
        int skip = Math.Min(ctx.SkipRows, total);
        if (skip >= total)
        {
            return;
        }

        int y = ctx.Rect.Y;
        if (Title is string title && skip == 0)
        {
            buffer.SetText(ctx.Rect.X, y, title, ChatPalette.Dim);
            if (height < 2)
            {
                return;
            }

            y += 1;
        }

        string glyphs = MapToGlyphs(_values, _maxOverride);
        if (glyphs.Length == 0)
        {
            buffer.SetText(ctx.Rect.X, y, EmptyText, ChatPalette.Dim);
            return;
        }

        int count = Math.Min(width, glyphs.Length);
        if (count > 0)
        {
            buffer.SetText(ctx.Rect.X, y, glyphs.AsSpan(0, count), ChatPalette.ToolArgs);
        }
    }

    public string RawText()
    {
        string glyphs = MapToGlyphs(_values, _maxOverride);
        if (glyphs.Length == 0)
        {
            glyphs = EmptyText;
        }

        return Title is null ? glyphs : Title + "\n" + glyphs;
    }

    /// <summary>
    /// Maps samples to block glyphs (pure, allocation-light): min..max scaled
    /// across <see cref="Levels" />; a flat series renders as
    /// <see cref="FlatGlyph" />. Non-positive widths are irrelevant here —
    /// clipping is the painter's job.
    /// </summary>
    public static string MapToGlyphs(IReadOnlyList<double> values, double? maxValue = null)
    {
        if (values.Count == 0)
        {
            return string.Empty;
        }

        double min = double.MaxValue;
        double max = double.MinValue;
        for (int i = 0; i < values.Count; i++)
        {
            double v = Sanitize(values[i]);
            if (v < min)
            {
                min = v;
            }

            if (v > max)
            {
                max = v;
            }
        }

        if (maxValue is double m && !double.IsNaN(m) && !double.IsInfinity(m) && m > 0)
        {
            // Explicit ceiling (ratatui sparkline `max`): scale from a zero
            // baseline so a half-max sample lands mid-range; data above the
            // ceiling raises it instead of clipping.
            min = Math.Min(min, 0);
            max = Math.Max(m, max);
        }

        if (!(max > min))
        {
            return new string(FlatGlyph, values.Count);
        }

        double span = max - min;
        var sb = new StringBuilder(values.Count);
        for (int i = 0; i < values.Count; i++)
        {
            double v = Sanitize(values[i]);
            double ratio = (v - min) / span;
            int level = (int)Math.Round(ratio * (Levels.Length - 1), MidpointRounding.AwayFromZero);
            sb.Append(Levels[Math.Clamp(level, 0, Levels.Length - 1)]);
        }

        return sb.ToString();
    }

    private static double Sanitize(double v)
    {
        if (double.IsNaN(v))
        {
            return 0;
        }

        if (double.IsPositiveInfinity(v))
        {
            return double.MaxValue / 2;
        }

        if (double.IsNegativeInfinity(v))
        {
            return 0;
        }

        return Math.Max(0, v);
    }
}
