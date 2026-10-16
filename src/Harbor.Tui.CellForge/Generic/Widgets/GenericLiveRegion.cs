using Ensure = Harbor.Abstractions.Extensions.Ensure;

namespace Harbor.Tui.CellForge.Generic.Widgets;

/// <summary>
/// Overflow policy for <see cref="GenericLiveRegion"/> (the Spectre.Console
/// <c>LiveDisplay</c> Overflow/Cropping half, as data: which lines survive
/// when the update target holds more than <see cref="GenericLiveRegion.MaxHeight"/>).
/// No cursor choreography travels with this — <c>LiveRenderable</c>'s
/// PositionCursor/RestoreCursor would break the FRONT==BACK invariant, so the
/// host repaints the region through <c>ScreenSession</c> frames instead.
/// </summary>
public enum LiveRegionOverflow
{
    /// <summary>Drop the oldest lines; the newest (tail) stay visible. Live-ticker default.</summary>
    ClipHead = 0,

    /// <summary>Drop the newest lines; the oldest (head) stay visible. Log-head default.</summary>
    ClipTail = 1,

    /// <summary>Keep the head and spend the last row on a "… +N more" marker.</summary>
    Ellipsize = 2,
}

/// <summary>
/// Progress-task math for <see cref="GenericLiveRegion"/> (the Spectre.Console
/// <c>ProgressTask</c> value half: completed/total → ratio/remaining/done).
/// Pure data, no refresh thread — the host advances <see cref="Completed"/>
/// and repaints; <c>RunExclusive</c> and <c>ProgressRefreshThread</c> stay
/// behind on purpose.
/// </summary>
/// <param name="Completed">Finished units (clamped into 0..Total on read).</param>
/// <param name="Total">Total units; ≤ 0 means unknown (ratio reads 0).</param>
public readonly record struct LiveProgressTask(double Completed, double Total)
{
    /// <summary>Clamped 0..1 fill ratio; 0 when <see cref="Total"/> is unknown or NaN.</summary>
    public double Ratio
    {
        get
        {
            if (double.IsNaN(Completed) || double.IsNaN(Total) || Total <= 0)
            {
                return 0;
            }

            return Math.Clamp(Completed / Total, 0, 1);
        }
    }

    /// <summary>Units left; never negative (unknown total reads <see cref="Total"/> clamped at 0).</summary>
    public double Remaining => Total <= 0 ? 0 : Math.Max(0, Total - Math.Max(0, Completed));

    /// <summary>True when the total is known and reached.</summary>
    public bool IsCompleted => Total > 0 && Completed >= Total;

    /// <summary>Whole-percent label ("42%").</summary>
    public string PercentText =>
        ((int)Math.Round(Ratio * 100, MidpointRounding.AwayFromZero)).ToString() + "%";

    /// <summary>Count label ("3/10"; unknown total reads "3/?").</summary>
    public string CountText
    {
        get
        {
            long done = (long)Math.Max(0, Math.Floor(Completed));
            return Total <= 0 ? done.ToString() + "/?" : done.ToString() + "/" + ((long)Math.Max(0, Total)).ToString();
        }
    }
}

/// <summary>
/// Live region as an abstraction (epic #1195, part of #1216): the update
/// target + overflow policy + pipe fallback, engine-independent. The host
/// pushes text in via <see cref="Update"/> and either paints the visible
/// window over cells or ships <see cref="RenderPlainText"/> down a pipe —
/// the Spectre <c>FallbackRenderer</c> split, minus the console.
/// No keys, no timers, no nullable public surface: nothing here joins the
/// KeyGate or WidgetState arch tables.
/// </summary>
public sealed class GenericLiveRegion
{
    private readonly List<string> _lines = new();

    /// <summary>Visible row budget; values below 1 coerce to 1. Default 8.</summary>
    public int MaxHeight { get; set; } = 8;

    /// <summary>Which lines survive when buffered lines exceed <see cref="MaxHeight"/>.</summary>
    public LiveRegionOverflow Overflow { get; set; } = LiveRegionOverflow.ClipHead;

    /// <summary>When true, <see cref="Complete"/> empties the buffer; otherwise it keeps the last frame.</summary>
    public bool AutoClear { get; set; }

    /// <summary>Buffered line count (pre-overflow).</summary>
    public int LineCount => _lines.Count;

    /// <summary>Painted height for the current buffer (overflow window, 0 when empty).</summary>
    public int DesiredHeight => _lines.Count == 0 ? 0 : Math.Min(EffectiveMaxHeight, _lines.Count);

    private int EffectiveMaxHeight => Math.Max(1, MaxHeight);

    /// <summary>Replaces the buffer with <paramref name="lines"/> (copied; null entries coerce to empty).</summary>
    public void Update(IReadOnlyList<string> lines)
    {
        Ensure.NotNull(lines);
        _lines.Clear();
        for (int i = 0; i < lines.Count; i++)
        {
            _lines.Add(lines[i] ?? string.Empty);
        }
    }

    /// <summary>Replaces the buffer with <paramref name="text"/> split on '\n' (trailing newline ignored).</summary>
    public void Update(string text)
    {
        Ensure.NotNull(text);
        _lines.Clear();
        int start = 0;
        for (int i = 0; i <= text.Length; i++)
        {
            if (i == text.Length || text[i] == '\n')
            {
                if (i > start || i < text.Length)
                {
                    _lines.Add(text.Substring(start, i - start).TrimEnd('\r'));
                }

                start = i + 1;
            }
        }
    }

    /// <summary>Empties the buffer.</summary>
    public void Clear() => _lines.Clear();

    /// <summary>Completion hook: clears when <see cref="AutoClear"/>, otherwise keeps the last frame.</summary>
    public void Complete()
    {
        if (AutoClear)
        {
            _lines.Clear();
        }
    }

    /// <summary>Visible window after <see cref="Overflow"/> (pure, for tests and <see cref="RenderPlainText"/>).</summary>
    public IReadOnlyList<string> VisibleLines()
    {
        int budget = EffectiveMaxHeight;
        if (_lines.Count <= budget)
        {
            return _lines.ToArray();
        }

        if (Overflow == LiveRegionOverflow.ClipTail)
        {
            var head = new string[budget];
            for (int i = 0; i < budget; i++)
            {
                head[i] = _lines[i];
            }

            return head;
        }

        if (Overflow == LiveRegionOverflow.Ellipsize)
        {
            var window = new string[budget];
            for (int i = 0; i < budget - 1; i++)
            {
                window[i] = _lines[i];
            }

            window[budget - 1] = "… +" + (_lines.Count - (budget - 1)).ToString() + " more";
            return window;
        }

        var tail = new string[budget];
        int skip = _lines.Count - budget;
        for (int i = 0; i < budget; i++)
        {
            tail[i] = _lines[skip + i];
        }

        return tail;
    }

    /// <summary>Pipe fallback: the visible window as plain text (one line per row, no styling).</summary>
    public string RenderPlainText() => string.Join("\n", VisibleLines());

    /// <summary>Paints the visible window at <paramref name="rect"/> (wide lines truncate with an ellipsis).</summary>
    /// <remarks>
    ///     No snapshot: <see cref="VisibleLines" /> allocates a window array on
    ///     every call, and <c>Paint</c> runs per frame while the region is
    ///     visible — the old path paid a <c>ToArray</c> per frame for a list
    ///     nothing else can touch mid-pass (owned by the render thread;
    ///     mutated only via <c>Update</c>/<c>Clear</c>/<c>Complete</c> on the
    ///     same pass, mirroring <c>ToastOverlay.Paint</c>). The window is
    ///     resolved with index math instead: same rows as
    ///     <see cref="VisibleLines" /> in every <see cref="LiveRegionOverflow" />
    ///     mode, zero allocation. <see cref="VisibleLines" /> stays the pure
    ///     API for tests and <c>RenderPlainText</c>.
    /// </remarks>
    public void Paint(ScreenBuffer buffer, Rect rect)
    {
        Ensure.NotNull(buffer);
        if (rect.Width <= 0 || rect.Height <= 0 || _lines.Count == 0)
        {
            return;
        }

        int budget = EffectiveMaxHeight;
        int start;
        string? marker = null;
        int count;
        if (_lines.Count <= budget)
        {
            start = 0;
            count = _lines.Count;
        }
        else if (Overflow == LiveRegionOverflow.ClipTail)
        {
            start = 0;
            count = budget;
        }
        else if (Overflow == LiveRegionOverflow.Ellipsize)
        {
            start = 0;
            count = budget;
            marker = "… +" + (_lines.Count - (budget - 1)).ToString() + " more";
        }
        else
        {
            start = _lines.Count - budget;
            count = budget;
        }

        var style = new CellStyle(ChatPalette.Text);
        int rows = Math.Min(count, rect.Height);
        for (int r = 0; r < rows; r++)
        {
            string line = marker is not null && r == count - 1 ? marker : _lines[start + r];
            buffer.SetText(rect.X, rect.Y + r, Fit(line, rect.Width), style);
        }
    }

    private static string Fit(string text, int width)
    {
        if (UnicodeWidth.Width(text.AsSpan()) <= width)
        {
            return text;
        }

        if (width <= 1)
        {
            return width == 1 ? "…" : string.Empty;
        }

        var span = text.AsSpan();
        int cells = 0;
        int i = 0;
        while (i < span.Length)
        {
            System.Text.Rune.DecodeFromUtf16(span[i..], out var rune, out int consumed);
            if (consumed <= 0)
            {
                break;
            }

            int w = Math.Max(1, UnicodeWidth.Width(rune));
            if (cells + w > width - 1)
            {
                break;
            }

            cells += w;
            i += consumed;
        }

        return text[..i] + "…";
    }
}

/// <summary>
/// Composable progress columns over <see cref="LiveProgressTask"/> (the
/// Spectre.Console <c>ProgressColumn</c> set minus the console: each column
/// is a pure string, the host joins and paints them). Glyphs reuse the
/// neutral idiom (<c>█/░</c> bar, braille spinner) shared with
/// <see cref="GenericProgressBar"/> — no chat <c>GaugeBar</c> coupling.
/// </summary>
public static class LiveColumns
{
    /// <summary>Filled cell.</summary>
    public const char Filled = '█';

    /// <summary>Track cell.</summary>
    public const char Track = '░';

    private static readonly string[] SpinnerFrames =
    [
        "⠋", "⠙", "⠹", "⠸", "⠼", "⠴", "⠦", "⠧", "⠇", "⠏",
    ];

    /// <summary>Spinner frame for the given monotonic tick (same set as the neutral spinner).</summary>
    public static string Spinner(long tick) =>
        SpinnerFrames[(int)((tick % SpinnerFrames.Length + SpinnerFrames.Length) % SpinnerFrames.Length)];

    /// <summary>Bar column: <paramref name="width"/> cells of fill/track for <paramref name="task"/>.</summary>
    public static string Bar(LiveProgressTask task, int width)
    {
        if (width <= 0)
        {
            return string.Empty;
        }

        int filled = GenericProgressBar.FilledCells(task.Ratio, width);
        filled = Math.Clamp(filled, 0, width);
        return new string(Filled, filled) + new string(Track, width - filled);
    }

    /// <summary>Percent column ("42%").</summary>
    public static string Percent(LiveProgressTask task) => task.PercentText;

    /// <summary>Count column ("3/10").</summary>
    public static string Count(LiveProgressTask task) => task.CountText;

    /// <summary>Joins non-empty columns with <paramref name="separator"/> (empty columns dropped).</summary>
    public static string Join(string separator, params string[] columns)
    {
        Ensure.NotNull(columns);
        var sb = new System.Text.StringBuilder();
        bool first = true;
        for (int i = 0; i < columns.Length; i++)
        {
            string col = columns[i] ?? string.Empty;
            if (col.Length == 0)
            {
                continue;
            }

            if (!first)
            {
                sb.Append(separator);
            }

            sb.Append(col);
            first = false;
        }

        return sb.ToString();
    }
}
