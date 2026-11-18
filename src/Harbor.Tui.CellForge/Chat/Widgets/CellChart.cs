using System.Globalization;
using System.Text;
using Harbor.Ui.Framework.Rendering.Widgets;
using Ensure = Harbor.Abstractions.Extensions.Ensure;

namespace Harbor.Tui.CellForge.Widgets;

/// <summary>How a <see cref="CellChart" /> draws its plot area.</summary>
public enum CellChartMode
{
    /// <summary>One vertical bar per column, growing from the baseline.</summary>
    Bars,

    /// <summary>One block-glyph row, painted by <see cref="SparklineBlock" />.</summary>
    Sparkline,
}

/// <summary>
/// Numeric series adapter for <see cref="CellChart" />: a finite sample vector
/// plus optional time labels, one per sample. Values are sanitized at the
/// chart boundary the same way <see cref="SparklineBlock" /> sanitizes them,
/// so a negative or non-finite sample can never corrupt the scale.
/// </summary>
/// <param name="Values">The samples, oldest first.</param>
/// <param name="TimeLabels">Optional caption per sample, used by the time axis.</param>
public readonly record struct ChartSeries(
    IReadOnlyList<double>? Values,
    IReadOnlyList<string>? TimeLabels = null)
{
    /// <summary>Sample count, zero for an uninitialized series.</summary>
    public int Count => Values?.Count ?? 0;

    /// <summary>Adapts an enumerable of numbers, materializing it once.</summary>
    public static ChartSeries From(IEnumerable<double> values, IEnumerable<string>? timeLabels = null)
    {
        Ensure.NotNull(values);
        return new ChartSeries(
            values.ToArray(),
            timeLabels is null ? null : timeLabels.ToArray());
    }

    /// <summary>
    /// The samples and captions inside <paramref name="window" />, clamped to
    /// the series bounds. This is the slice the time zoom renders.
    /// </summary>
    public ChartSeries Slice(ChartWindow window)
    {
        var w = CellChart.NormalizeWindow(this, window);
        if (w.Count <= 0 || Values is null)
        {
            return default;
        }

        double[] values = new double[w.Count];
        for (int i = 0; i < w.Count; i++)
        {
            values[i] = Values[w.Start + i];
        }

        string[]? labels = null;
        if (TimeLabels is not null)
        {
            labels = new string[w.Count];
            for (int i = 0; i < w.Count; i++)
            {
                int at = w.Start + i;
                labels[i] = at < TimeLabels.Count ? TimeLabels[at] : string.Empty;
            }
        }

        return new ChartSeries(values, labels);
    }
}

/// <summary>
/// A zoom window into a <see cref="ChartSeries" />: first visible sample and
/// visible sample count. The chart clamps both to the series bounds.
/// </summary>
/// <param name="Start">Index of the first visible sample.</param>
/// <param name="Count">Number of visible samples.</param>
public readonly record struct ChartWindow(int Start, int Count);

/// <summary>
/// Externally owned state of a <see cref="CellChart" />. The caller folds
/// every zoom or pan into a new value, so the widget keeps no mutable state.
/// </summary>
/// <param name="Series">The numeric series to draw.</param>
/// <param name="Mode">Bars or sparkline.</param>
/// <param name="Window">Optional zoom window; null draws the whole series.</param>
/// <param name="Title">Optional title painted above the plot.</param>
/// <param name="YTicks">Number of value-axis labels, from 2 to 5.</param>
public readonly record struct CellChartState(
    ChartSeries Series,
    CellChartMode Mode = CellChartMode.Bars,
    ChartWindow? Window = null,
    string? Title = null,
    int YTicks = 3);

/// <summary>
/// Cell-native chart on numeric data (gpui-kit Chart contract, #2382). Two
/// modes share one layout: a value axis on the left, a time axis at the
/// bottom, and a plot in between. Bars grow from a zero baseline; the
/// sparkline delegates glyph mapping to <see cref="SparklineBlock" /> and
/// bars use the filled glyph from <see cref="GaugeBar" />. The time window
/// is a pure <see cref="ChartWindow" /> the host zooms and pans. Pie, radar,
/// candlestick and sankey need 2D geometry and stay out of scope.
/// </summary>
public static class CellChart
{
    /// <summary>Minimum number of value-axis labels.</summary>
    public const int MinYTicks = 2;

    /// <summary>Maximum number of value-axis labels.</summary>
    public const int MaxYTicks = 5;

    /// <summary>Non-negative finite sample. NaN and -Infinity become 0.</summary>
    public static double Sanitize(double v) =>
        double.IsNaN(v) || double.IsNegativeInfinity(v) || v < 0 ? 0
        : double.IsPositiveInfinity(v) ? double.MaxValue / 2
        : v;

    /// <summary>
    /// Clamps a zoom window to the series: start in [0, count-1], count in
    /// [1, count-start]. An empty series yields a zero window.
    /// </summary>
    public static ChartWindow NormalizeWindow(ChartSeries series, ChartWindow? window)
    {
        int total = series.Count;
        if (total <= 0)
        {
            return default;
        }

        int start = Math.Clamp(window?.Start ?? 0, 0, total - 1);
        int count = Math.Clamp(window?.Count ?? total, 1, total - start);
        return new ChartWindow(start, count);
    }

    /// <summary>Sanitized samples in the current window.</summary>
    public static IReadOnlyList<double> VisibleValues(CellChartState state)
    {
        var w = NormalizeWindow(state.Series, state.Window);
        if (w.Count <= 0)
        {
            return Array.Empty<double>();
        }

        var values = state.Series.Values;
        if (values is null)
        {
            return Array.Empty<double>();
        }

        var result = new double[w.Count];
        for (int i = 0; i < w.Count; i++)
        {
            result[i] = Sanitize(values[w.Start + i]);
        }

        return result;
    }

    /// <summary>
    /// Zero-based scale over the visible samples. The chart always starts at
    /// zero so bar heights are comparable; a flat zero series gets a unit
    /// ceiling instead of a divide-by-zero span.
    /// </summary>
    public static (double Min, double Max) Scale(IReadOnlyList<double> values)
    {
        double max = 0;
        for (int i = 0; i < values.Count; i++)
        {
            double v = Sanitize(values[i]);
            if (v > max)
            {
                max = v;
            }
        }

        return (0, max > 0 ? max : 1);
    }

    /// <summary>Invariant tick label, with no trailing zeros.</summary>
    public static string FormatTick(double value) =>
        value.ToString("0.##", CultureInfo.InvariantCulture);

    /// <summary>
    /// Value-axis labels, high to low. The first is the scale ceiling, the
    /// last is the baseline.
    /// </summary>
    public static string[] YLabels(CellChartState state)
    {
        int ticks = Math.Clamp(state.YTicks, MinYTicks, MaxYTicks);
        var (min, max) = Scale(VisibleValues(state));
        var labels = new string[ticks];
        for (int i = 0; i < ticks; i++)
        {
            double v = max - ((max - min) * i / (ticks - 1));
            labels[i] = FormatTick(v);
        }

        return labels;
    }

    /// <summary>
    /// Time-axis captions for the visible window: the first and last sample.
    /// A series without labels falls back to the sample indices.
    /// </summary>
    public static (string Left, string Right) XLabels(CellChartState state)
    {
        var w = NormalizeWindow(state.Series, state.Window);
        if (w.Count <= 0)
        {
            return (string.Empty, string.Empty);
        }

        var labels = state.Series.TimeLabels;
        int last = w.Start + w.Count - 1;
        string left = labels is not null && w.Start < labels.Count
            ? labels[w.Start]
            : w.Start.ToString(CultureInfo.InvariantCulture);
        string right = labels is not null && last < labels.Count
            ? labels[last]
            : last.ToString(CultureInfo.InvariantCulture);
        return (left, right);
    }

    /// <summary>
    /// Reduces many samples to at most <paramref name="columns" /> values by
    /// taking the maximum in each bucket. A zoomed-out chart keeps its spikes.
    /// </summary>
    public static double[] Bucket(IReadOnlyList<double> values, int columns)
    {
        if (columns <= 0 || values.Count == 0)
        {
            return Array.Empty<double>();
        }

        var result = new double[columns];
        for (int c = 0; c < columns; c++)
        {
            int from = (c * values.Count) / columns;
            int to = Math.Max(from + 1, ((c + 1) * values.Count) / columns);
            double peak = 0;
            for (int i = from; i < to && i < values.Count; i++)
            {
                double v = Sanitize(values[i]);
                if (v > peak)
                {
                    peak = v;
                }
            }

            result[c] = peak;
        }

        return result;
    }

    /// <summary>Full block rows for one bar, rounded against the plot height.</summary>
    public static int BarRows(double value, double max, int rows)
    {
        if (rows <= 0 || max <= 0)
        {
            return 0;
        }

        double ratio = Math.Clamp(Sanitize(value) / max, 0, 1);
        return (int)Math.Round(ratio * rows, MidpointRounding.AwayFromZero);
    }

    /// <summary>Halves the visible count around its center, never below one sample.</summary>
    public static CellChartState ZoomIn(CellChartState state)
    {
        var w = NormalizeWindow(state.Series, state.Window);
        if (w.Count <= 1)
        {
            return state;
        }

        int count = Math.Max(1, w.Count / 2);
        return ShiftWindow(state, w, count);
    }

    /// <summary>Doubles the visible count around its center, never past the series.</summary>
    public static CellChartState ZoomOut(CellChartState state)
    {
        var w = NormalizeWindow(state.Series, state.Window);
        int count = Math.Min(state.Series.Count, Math.Max(1, w.Count * 2));
        return count == w.Count ? state : ShiftWindow(state, w, count);
    }

    /// <summary>Moves the visible window by <paramref name="delta" /> samples.</summary>
    public static CellChartState Pan(CellChartState state, int delta)
    {
        var w = NormalizeWindow(state.Series, state.Window);
        if (delta == 0 || state.Series.Count <= 0)
        {
            return state;
        }

        int start = Math.Clamp(w.Start + delta, 0, state.Series.Count - w.Count);
        return start == w.Start ? state : state with { Window = new ChartWindow(start, w.Count) };
    }

    /// <summary>
    /// Paints the chart into <paramref name="rect" />. Default styles come
    /// from <see cref="ChatPalette" />. A degenerate rect paints nothing.
    /// </summary>
    public static void Paint(
        ScreenBuffer buffer,
        Rect rect,
        CellChartState state,
        CellStyle bar = default,
        CellStyle axis = default,
        CellStyle title = default)
    {
        Ensure.NotNull(buffer);
        if (rect.Width <= 0 || rect.Height <= 0)
        {
            return;
        }

        CellStyle barStyle = bar.Equals(default) ? new CellStyle(ChatPalette.Accent) : bar;
        CellStyle axisStyle = axis.Equals(default) ? ChatPalette.Dim : axis;
        CellStyle titleStyle = title.Equals(default) ? ChatPalette.ToolArgs : title;

        int x0 = rect.X;
        int y0 = rect.Y;
        int row = y0;
        if (!string.IsNullOrWhiteSpace(state.Title))
        {
            buffer.SetText(x0, row, state.Title!.Trim(), titleStyle);
            row++;
        }

        var values = VisibleValues(state);
        string[] yLabels = YLabels(state);
        int axisWidth = 0;
        for (int i = 0; i < yLabels.Length; i++)
        {
            axisWidth = Math.Max(axisWidth, UnicodeWidth.Width(yLabels[i]));
        }

        int plotX = x0 + axisWidth + (axisWidth > 0 ? 1 : 0);
        int plotWidth = Math.Max(0, x0 + rect.Width - plotX);

        int remaining = y0 + rect.Height - row;
        int timeRows = remaining >= 3 ? 1 : 0;
        int plotRows = Math.Max(0, remaining - timeRows);
        if (plotRows <= 0)
        {
            return;
        }

        int ticks = yLabels.Length;
        for (int i = 0; i < ticks; i++)
        {
            int ly = plotRows == 1
                ? row
                : row + (int)Math.Round(i * (plotRows - 1) / (double)(ticks - 1), MidpointRounding.AwayFromZero);
            int labelWidth = UnicodeWidth.Width(yLabels[i]);
            buffer.SetText(x0 + axisWidth - labelWidth, ly, yLabels[i], axisStyle);
        }

        var (_, max) = Scale(values);
        if (state.Mode == CellChartMode.Sparkline)
        {
            if (values.Count > 0 && plotWidth > 0)
            {
                string glyphs = SparklineBlock.MapToGlyphs(values, max);
                int count = Math.Min(plotWidth, glyphs.Length);
                buffer.SetText(plotX, row + plotRows - 1, glyphs.AsSpan(0, count), barStyle);
            }
        }
        else
        {
            double[] columns = Bucket(values, plotWidth);
            var rune = new Rune(GaugeBar.Filled);
            for (int c = 0; c < columns.Length; c++)
            {
                int height = BarRows(columns[c], max, plotRows);
                for (int k = 0; k < height; k++)
                {
                    buffer.SetRune(plotX + c, row + plotRows - 1 - k, rune, barStyle);
                }
            }
        }

        if (timeRows == 1)
        {
            var (left, right) = XLabels(state);
            buffer.SetText(x0, row + plotRows, left, axisStyle);
            int rightWidth = UnicodeWidth.Width(right);
            if (rightWidth > 0 && rightWidth <= rect.Width)
            {
                buffer.SetText(x0 + rect.Width - rightWidth, row + plotRows, right, axisStyle);
            }
        }
    }

    private static CellChartState ShiftWindow(CellChartState state, ChartWindow current, int count)
    {
        int center = current.Start + ((current.Count - 1) / 2);
        int start = Math.Clamp(center - ((count - 1) / 2), 0, state.Series.Count - count);
        return state with { Window = new ChartWindow(start, count) };
    }
}
