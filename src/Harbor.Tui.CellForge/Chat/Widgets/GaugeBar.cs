namespace Harbor.Tui.CellForge.Widgets;

/// <summary>
/// Cell-native progress state for <see cref="GaugeBar" /> (ratatui
/// <c>Gauge</c> pattern): a clamped 0..1 fill ratio plus an optional label.
/// Null label renders the default percent text ("42%"); empty label hides it.
/// </summary>
public readonly record struct GaugeState(double Ratio, string? Label = null, bool ShowLabel = true);

/// <summary>
/// Cell-native horizontal progress bar (ratatui <c>gauge.rs</c> reference):
/// a single-row <c>filled × N + track × (width − N)</c> run with the label
/// centered on top. Label cells over the filled run use
/// <paramref name="labelOnFill" />, over the track
/// <paramref name="labelOnTrack" /> — the same two-tone split ratatui draws.
/// Host-agnostic: the StatusPanel context bar adopts this for ctx% (UX9
/// suffix), task cards for step progress (UX5 suffix), the approval modal
/// for quota display (UX7). Paints nothing when the rect is degenerate.
/// </summary>
public static class GaugeBar
{
    /// <summary>Filled cell (ratatui uses a space with fill bg; full block reads better on plain cells).</summary>
    public const char Filled = '█';

    /// <summary>Track cell.</summary>
    public const char Track = '░';

    /// <summary>Clamps to [0,1]; NaN and negatives become 0, infinities become 1.</summary>
    public static double ClampRatio(double ratio) =>
        double.IsNaN(ratio) ? 0 : Math.Clamp(ratio, 0, 1);

    /// <summary>Visible label text for <paramref name="state" /> (percent when null).</summary>
    public static string LabelText(GaugeState state) =>
        state.Label ?? ((int)Math.Round(ClampRatio(state.Ratio) * 100, MidpointRounding.AwayFromZero) + "%");

    /// <summary>Filled cell count for <paramref name="width" /> columns.</summary>
    public static int FilledCells(double ratio, int width) =>
        width <= 0 ? 0 : (int)Math.Round(ClampRatio(ratio) * width, MidpointRounding.AwayFromZero);

    /// <summary>
    /// Paints the gauge into the first row of <paramref name="rect" />.
    /// <paramref name="fill"/> colors the filled run, <paramref name="track"/>
    /// the remainder; label styles split by side as in ratatui.
    /// </summary>
    public static void Paint(
        ScreenBuffer buffer,
        Rect rect,
        GaugeState state,
        CellStyle fill = default,
        CellStyle track = default,
        CellStyle labelOnFill = default,
        CellStyle labelOnTrack = default)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        if (rect.Width <= 0 || rect.Height <= 0)
        {
            return;
        }

        int y = rect.Y;
        int x0 = rect.X;
        int width = rect.Width;
        int filled = FilledCells(state.Ratio, width);

        bool fillDefault = fill.Equals(default);
        CellStyle fillStyle = fillDefault ? new CellStyle(ChatPalette.Accent) : fill;
        CellStyle trackStyle = track.Equals(default) ? ChatPalette.Dim : track;

        if (filled > 0)
        {
            buffer.SetText(x0, y, new string(Filled, filled), fillStyle);
        }

        if (filled < width)
        {
            buffer.SetText(x0 + filled, y, new string(Track, width - filled), trackStyle);
        }

        if (!state.ShowLabel)
        {
            return;
        }

        string label = LabelText(state);
        if (string.IsNullOrEmpty(label))
        {
            return;
        }

        int labelWidth = UnicodeWidth.Width(label);
        if (labelWidth <= 0 || labelWidth > width)
        {
            return;
        }

        int lx = x0 + ((width - labelWidth) / 2);
        CellStyle onFill = labelOnFill.Equals(default)
            ? new CellStyle(ChatPalette.Accent, attrs: StyleAttr.Bold)
            : labelOnFill;
        CellStyle onTrack = labelOnTrack.Equals(default) ? ChatPalette.Dim : labelOnTrack;

        // Per-rune paint so the style flips exactly at the fill boundary,
        // including when the boundary lands mid-label.
        var span = label.AsSpan();
        int cursor = lx;
        while (!span.IsEmpty && cursor < x0 + width)
        {
            System.Text.Rune.DecodeFromUtf16(span, out var rune, out int consumed);
            if (consumed <= 0)
            {
                break;
            }

            bool overFill = cursor < x0 + filled;
            buffer.SetText(cursor, y, span[..consumed], overFill ? onFill : onTrack);
            cursor += Math.Max(1, UnicodeWidth.Width(rune));
            span = span[consumed..];
        }
    }
}
