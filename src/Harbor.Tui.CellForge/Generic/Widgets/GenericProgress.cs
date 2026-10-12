namespace Harbor.Tui.CellForge.Generic.Widgets;

/// <summary>
/// Generic progress state (Termina <c>ProgressBar</c> slot, epic #1195):
/// a clamped 0..1 ratio plus an optional label. Null label renders the
/// default percent text; empty label hides it.
/// </summary>
public readonly record struct GenericProgressState(double Ratio, string? Label = null, bool ShowLabel = true);

/// <summary>
/// Generic horizontal progress bar over cells. Same block-glyph idiom as the
/// chat gauge (filled + track run, label centered with a two-tone split) but
/// owned by the neutral set: no status-panel or task-card coupling. Hosts
/// that need chat progress keep using the chat primitive; everything else
/// paints this.
/// </summary>
public static class GenericProgressBar
{
    /// <summary>Filled cell.</summary>
    public const char Filled = '█';

    /// <summary>Track cell.</summary>
    public const char Track = '░';

    /// <summary>Clamps to [0,1]; NaN and negatives become 0, infinities become 1.</summary>
    public static double ClampRatio(double ratio) =>
        double.IsNaN(ratio) ? 0 : Math.Clamp(ratio, 0, 1);

    /// <summary>Visible label text for <paramref name="state"/> (percent when null).</summary>
    public static string LabelText(GenericProgressState state) =>
        state.Label ?? ((int)Math.Round(ClampRatio(state.Ratio) * 100, MidpointRounding.AwayFromZero) + "%");

    /// <summary>Filled cell count for <paramref name="width"/> columns.</summary>
    public static int FilledCells(double ratio, int width) =>
        width <= 0 ? 0 : (int)Math.Round(ClampRatio(ratio) * width, MidpointRounding.AwayFromZero);

    /// <summary>Paints the bar into the first row of <paramref name="rect"/>.</summary>
    public static void Paint(
        ScreenBuffer buffer,
        Rect rect,
        GenericProgressState state,
        CellStyle fill = default,
        CellStyle track = default)
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

        CellStyle fillStyle = fill.Equals(default) ? new CellStyle(ChatPalette.Accent) : fill;
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
        var onFill = new CellStyle(ChatPalette.Accent, attrs: StyleAttr.Bold);
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
            buffer.SetText(cursor, y, span[..consumed], overFill ? onFill : trackStyle);
            cursor += Math.Max(1, UnicodeWidth.Width(rune));
            span = span[consumed..];
        }
    }
}

/// <summary>
/// Generic indeterminate spinner over cells (braille frames, ratatui throbber
/// idiom): one cell that cycles while a ratio is unknown. Owns only its frame
/// index; the host advances it on a tick and repaints.
/// </summary>
public sealed class GenericSpinner
{
    private static readonly char[] Frames = ['⠋', '⠙', '⠹', '⠸', '⠼', '⠴', '⠦', '⠧', '⠇', '⠏'];

    private int _frame;

    /// <summary>Current frame index.</summary>
    public int Frame => _frame;

    /// <summary>Number of frames per cycle.</summary>
    public static int FrameCount => Frames.Length;

    /// <summary>Advances one frame (wraps).</summary>
    public void Advance() => _frame = (_frame + 1) % Frames.Length;

    /// <summary>Paints the current frame at the top-left of <paramref name="rect"/>.</summary>
    public void Paint(ScreenBuffer buffer, Rect rect, CellStyle style = default)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        if (rect.Width <= 0 || rect.Height <= 0)
        {
            return;
        }

        CellStyle s = style.Equals(default) ? new CellStyle(ChatPalette.Accent) : style;
        buffer.SetText(rect.X, rect.Y, Frames[_frame].ToString(), s);
    }
}
