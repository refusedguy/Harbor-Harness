using System.Globalization;
using System.Text;
using Harbor.Ui.Framework.Rendering;

namespace Harbor.Ui.Framework.Rendering.Widgets;

/// <summary>One bar of a <see cref="CostBarsBlock" />: a trimmed label plus a non-negative value.</summary>
public readonly record struct CostBar(string Label, double Value);

/// <summary>
/// Horizontal cost-bar chart ([PRIM10], part of #305): a ratatui
/// <c>chart.rs</c> / bubbles-progress port — one row per bucket, label plus a
/// <c>█</c>/<c>░</c> fill scaled to the max value, plus a compact value suffix.
/// Host-agnostic and immutable: the owner feeds per-model (or per-turn) cost
/// buckets and marks the slot dirty on update. An optional title row labels
/// the panel (e.g. <c>"cost $"</c>).
/// </summary>
public sealed class CostBarsBlock : IChatBlock
{
    private const char Filled = '█';
    private const char Empty = '░';

    /// <summary>Placeholder painted when there are no bars.</summary>
    public const string EmptyText = "(empty)";

    private readonly CostBar[] _bars;
    private readonly double _max;

    /// <summary>Bars in order (labels trimmed, values clamped to ≥ 0).</summary>
    public IReadOnlyList<CostBar> Bars => _bars;

    /// <summary>Optional title painted dim on the row above the bars.</summary>
    public string? Title { get; }

    /// <summary>Scale denominator: explicit max or the largest bar value.</summary>
    public double MaxValue => _max;

    public CostBarsBlock(IEnumerable<CostBar>? bars = null, string? title = null, double? maxValue = null)
    {
        if (bars is null)
        {
            _bars = [];
        }
        else
        {
            var list = new List<CostBar>();
            foreach (var b in bars)
            {
                list.Add(new CostBar(
                    string.IsNullOrWhiteSpace(b.Label) ? "?" : b.Label.Trim(),
                    Sanitize(b.Value)));
            }

            _bars = [.. list];
        }

        Title = string.IsNullOrWhiteSpace(title) ? null : title.Trim();

        double peak = 0;
        for (int i = 0; i < _bars.Length; i++)
        {
            if (_bars[i].Value > peak)
            {
                peak = _bars[i].Value;
            }
        }

        _max = maxValue is double m && !double.IsNaN(m) && !double.IsInfinity(m) && m > 0
            ? Math.Max(m, peak)
            : peak;
    }

    public string Kind => "cost-bars";

    public bool IsStreamContinuation => false;

    public int BudgetBytes
    {
        get
        {
            int bytes = 64;
            for (int i = 0; i < _bars.Length; i++)
            {
                bytes += 16 + (_bars[i].Label.Length * 2);
            }

            return bytes + ((Title?.Length ?? 0) * 2);
        }
    }

    public BlockMeasure Measure(int width) =>
        BlockMeasure.Exact((Title is null ? 0 : 1) + Math.Max(1, _bars.Length));

    public int CheapEstimate(int width) => (Title is null ? 0 : 1) + Math.Max(1, _bars.Length);

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
        int total = (titled ? 1 : 0) + Math.Max(1, _bars.Length);
        int skip = Math.Min(ctx.SkipRows, total);
        int rows = Math.Min(height, total - skip);
        if (rows <= 0)
        {
            return;
        }

        int y = ctx.Rect.Y;
        int row = skip;
        for (int i = 0; i < rows; i++, row++, y++)
        {
            if (Title is string title && row == 0)
            {
                buffer.SetText(ctx.Rect.X, y, title, ChatPalette.Dim);
                continue;
            }

            int bar = row - (titled ? 1 : 0);
            if (_bars.Length == 0)
            {
                buffer.SetText(ctx.Rect.X, y, EmptyText, ChatPalette.Dim);
                continue;
            }

            string line = RenderRow(_bars[bar].Label, _bars[bar].Value, _max, width);
            int cut = line.IndexOf(' ', StringComparison.Ordinal);
            if (cut < 0)
            {
                buffer.SetText(ctx.Rect.X, y, line, ChatPalette.ToolArgs);
                continue;
            }

            buffer.SetText(ctx.Rect.X, y, line.AsSpan(0, cut), ChatPalette.ToolArgs);
            int x = ctx.Rect.X + cut;
            int right = ctx.Rect.Right;
            if (x < right)
            {
                buffer.SetText(x, y, line.AsSpan(cut, Math.Min(line.Length - cut, right - x)), ChatPalette.Dim);
            }
        }
    }

    public string RawText()
    {
        if (_bars.Length == 0)
        {
            return Title is null ? EmptyText : Title + "\n" + EmptyText;
        }

        var sb = new StringBuilder();
        if (Title is not null)
        {
            sb.Append(Title).Append('\n');
        }

        for (int i = 0; i < _bars.Length; i++)
        {
            if (i > 0)
            {
                sb.Append('\n');
            }

            sb.Append(_bars[i].Label).Append(' ').Append(FormatValue(_bars[i].Value));
        }

        return sb.ToString();
    }

    /// <summary>
    /// Renders one bar row (pure): <c>"label ███░░ value"</c> clipped to
    /// <paramref name="width" /> cells. Fill is proportional to
    /// <c>value / max</c>; a zero max renders an empty track.
    /// </summary>
    public static string RenderRow(string label, double value, double max, int width)
    {
        string name = string.IsNullOrWhiteSpace(label) ? "?" : label.Trim();
        value = Sanitize(value);
        max = Sanitize(max);
        width = Math.Max(1, width);

        string suffix = " " + FormatValue(value);
        int prefix = name.Length + 1; // "label "
        int track = width - prefix - suffix.Length;
        if (track <= 0)
        {
            string head = name.Length >= width ? name[..width] : name;
            return head;
        }

        int filled = max <= 0 ? 0 : (int)Math.Round((value / max) * track, MidpointRounding.AwayFromZero);
        filled = Math.Clamp(filled, 0, track);
        return name + " " + new string(Filled, filled) + new string(Empty, track - filled) + suffix;
    }

    private static string FormatValue(double v) =>
        v.ToString("0.##", CultureInfo.InvariantCulture);

    private static double Sanitize(double v)
    {
        if (double.IsNaN(v) || double.IsNegativeInfinity(v))
        {
            return 0;
        }

        if (double.IsPositiveInfinity(v))
        {
            return double.MaxValue / 2;
        }

        return Math.Max(0, v);
    }
}
