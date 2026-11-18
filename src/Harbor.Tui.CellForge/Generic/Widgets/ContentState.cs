using System.Text;
using Ensure = Harbor.Abstractions.Extensions.Ensure;

namespace Harbor.Tui.CellForge.Generic.Widgets;

/// <summary>
/// Which presentation a content region shows. The three states are the whole
/// contract: a host swaps one value and the same painter covers loading,
/// empty and ready without any session, task or chat knowledge.
/// </summary>
public enum ContentPresentation : byte
{
    /// <summary>Painting placeholder rows with a shimmer sweep.</summary>
    Loading = 0,

    /// <summary>Painting an empty message and an optional action hint.</summary>
    Empty = 1,

    /// <summary>Data is present. The host paints its own content; this painter is a no-op.</summary>
    Ready = 2,
}

/// <summary>
/// The shared style contract for all three content-state presentations. Every
/// style comes from <see cref="ChatPalette"/> tokens (HDS v1), so a theme switch
/// is picked up on the next paint. Passing <c>default</c> selects
/// <see cref="Default"/>, the same convention as
/// <see cref="GenericProgressBar.Paint"/>.
/// </summary>
/// <param name="Skeleton">Base fill of a placeholder row.</param>
/// <param name="Shimmer">Highlight style of the moving window.</param>
/// <param name="EmptyText">Style of the empty-state message.</param>
/// <param name="EmptyAction">Style of the optional action hint.</param>
public readonly record struct ContentStateStyle(
    CellStyle Skeleton,
    CellStyle Shimmer,
    CellStyle EmptyText,
    CellStyle EmptyAction)
{
    /// <summary>Palette-derived styles, recomputed on each access so a theme change is never cached.</summary>
    public static ContentStateStyle Default => new(
        new CellStyle(ChatPalette.Muted, attrs: StyleAttr.Dim),
        new CellStyle(ChatPalette.Text, attrs: StyleAttr.Bold),
        ChatPalette.Dim,
        new CellStyle(ChatPalette.Accent, attrs: StyleAttr.Bold));

    /// <summary>Explicit styles win; an all-default value resolves to <see cref="Default"/>.</summary>
    public ContentStateStyle Resolve() => this == default ? Default : this;
}

/// <summary>
/// Domain-independent state of a content region: one presentation plus the
/// small payload each presentation needs. The host owns the value and passes a
/// frame tick to <see cref="Paint"/>; no timer, store or message is involved.
/// </summary>
/// <param name="Presentation">Presentation to paint.</param>
/// <param name="SkeletonRows">Placeholder row count for <see cref="ContentPresentation.Loading"/>.</param>
/// <param name="EmptyText">Message for <see cref="ContentPresentation.Empty"/>.</param>
/// <param name="EmptyAction">Optional action hint for <see cref="ContentPresentation.Empty"/>.</param>
public readonly record struct ContentState(
    ContentPresentation Presentation,
    int SkeletonRows = 3,
    string? EmptyText = null,
    string? EmptyAction = null)
{
    /// <summary>Placeholder row count used when the caller does not choose one.</summary>
    public const int DefaultSkeletonRows = 3;

    /// <summary>Message used when an empty state supplies no text.</summary>
    public const string DefaultEmptyText = "Nothing here yet";

    /// <summary>Creates a loading state, clamped to a non-negative row count.</summary>
    public static ContentState Loading(int skeletonRows = 3) =>
        new(ContentPresentation.Loading, Math.Max(0, skeletonRows));

    /// <summary>Creates an empty state with a default message and an optional action hint.</summary>
    public static ContentState Empty(string? text = null, string? action = null) =>
        new(ContentPresentation.Empty, 0, text ?? DefaultEmptyText, action);

    /// <summary>Creates a ready state. The host paints the real content.</summary>
    public static ContentState Ready() => new(ContentPresentation.Ready);

    /// <summary>True while the loading presentation is selected.</summary>
    public bool IsLoading => Presentation == ContentPresentation.Loading;

    /// <summary>True while the empty presentation is selected.</summary>
    public bool IsEmpty => Presentation == ContentPresentation.Empty;

    /// <summary>True while the ready presentation is selected.</summary>
    public bool IsReady => Presentation == ContentPresentation.Ready;

    /// <summary>
    /// Paints the selected presentation into <paramref name="rect"/>. The tick
    /// is the host's monotonic frame counter (the 80 ms SpinnerStrip rhythm is
    /// the intended cadence); the shimmer is a pure function of it, so cell-diff
    /// sees only the cells whose style moved.
    /// </summary>
    public void Paint(ScreenBuffer buffer, Rect rect, long tick, ContentStateStyle style = default)
    {
        Ensure.NotNull(buffer);
        if (rect.Width <= 0 || rect.Height <= 0)
        {
            return;
        }

        ContentStateStyle resolved = style.Resolve();
        switch (Presentation)
        {
            case ContentPresentation.Loading:
                PaintSkeleton(buffer, rect, Math.Max(0, SkeletonRows), tick, resolved);
                break;
            case ContentPresentation.Empty:
                PaintEmpty(buffer, rect, resolved);
                break;
            case ContentPresentation.Ready:
            default:
                // The host owns real content. Painting here would overwrite it.
                break;
        }
    }
}

/// <summary>
/// Loading presentation: gray placeholder rows plus a shimmer window that
/// sweeps one cell per tick by changing SGR attributes only. The glyph under
/// the window never changes, so a frame touches at most
/// <c>2 * ShimmerWindow</c> cell styles (including the wrap frame).
/// </summary>
public static class ContentSkeleton
{
    /// <summary>Placeholder glyph. Width one in every terminal, unlike box drawing.</summary>
    public const char Glyph = '░';

    /// <summary>Width of the shimmer window in cells.</summary>
    public const int ShimmerWindow = 6;

    // A bounded run avoids allocating a new string per row per frame.
    private const string Run = "░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░";

    /// <summary>
    /// Row widths form a staircase: row <c>i</c> of <paramref name="rows"/>
    /// spans <c>width - i * width / (rows + 1)</c> cells. Always at least one
    /// cell while the width is positive, and never wider than the rect.
    /// </summary>
    public static int RowWidth(int width, int row, int rows)
    {
        if (width <= 0 || row < 0 || row >= rows)
        {
            return 0;
        }

        return width - ((row * width) / (rows + 1));
    }

    /// <summary>
    /// Shimmer window for one tick: the window stays fully inside the row and
    /// wraps after the last position. Deterministic for negative ticks too.
    /// </summary>
    public static (int Start, int Length) ShimmerSpan(int rowWidth, long tick)
    {
        if (rowWidth <= 0)
        {
            return (0, 0);
        }

        int window = Math.Min(ShimmerWindow, rowWidth);
        int travel = rowWidth - window;
        if (travel == 0)
        {
            return (0, window);
        }

        long period = travel + 1L;
        long phase = ((tick % period) + period) % period;
        return ((int)phase, window);
    }

    /// <summary>Paints the placeholder rows, then overlays the shimmer window by style only.</summary>
    public static void Paint(
        ScreenBuffer buffer,
        Rect rect,
        int rows,
        long tick,
        ContentStateStyle style)
    {
        Ensure.NotNull(buffer);
        if (rect.Width <= 0 || rect.Height <= 0 || rows <= 0)
        {
            return;
        }

        int rowCount = Math.Min(rows, rect.Height);
        for (int row = 0; row < rowCount; row++)
        {
            int width = Math.Min(RowWidth(rect.Width, row, rows), rect.Width);
            int y = rect.Y + row;

            int x = rect.X;
            int remaining = width;
            while (remaining > 0)
            {
                int take = Math.Min(remaining, Run.Length);
                buffer.SetText(x, y, Run.AsSpan(0, take), style.Skeleton);
                x += take;
                remaining -= take;
            }

            (int start, int length) = ShimmerSpan(width, tick);
            for (int i = 0; i < length; i++)
            {
                buffer.SetStyleAt(rect.X + start + i, y, style.Shimmer);
            }
        }
    }
}

/// <summary>
/// Empty presentation: a centered message on the first row and an optional
/// centered action hint on the second. Both are clipped to the rect by display
/// width, so a wide rune is never split across cells.
/// </summary>
public static class ContentEmpty
{
    /// <summary>Paints the message and, when the rect is tall enough, the action hint.</summary>
    public static void Paint(
        ScreenBuffer buffer,
        Rect rect,
        string? text,
        string? action,
        ContentStateStyle style)
    {
        Ensure.NotNull(buffer);
        if (rect.Width <= 0 || rect.Height <= 0)
        {
            return;
        }

        string message = string.IsNullOrEmpty(text) ? ContentState.DefaultEmptyText : text;
        PaintCentered(buffer, rect, rect.Y, message, style.EmptyText);

        if (rect.Height >= 2 && !string.IsNullOrEmpty(action))
        {
            PaintCentered(buffer, rect, rect.Y + 1, action!, style.EmptyAction);
        }
    }

    private static void PaintCentered(
        ScreenBuffer buffer,
        Rect rect,
        int y,
        string text,
        CellStyle style)
    {
        int take = FitPrefix(text, rect.Width);
        if (take == 0)
        {
            return;
        }

        var span = text.AsSpan(0, take);
        int width = UnicodeWidth.Width(span);
        int x = rect.X + Math.Max(0, (rect.Width - width) / 2);
        buffer.SetText(x, y, span, style);
    }

    // Longest prefix of text that fits maxWidth display columns.
    private static int FitPrefix(string text, int maxWidth)
    {
        if (maxWidth <= 0)
        {
            return 0;
        }

        var span = text.AsSpan();
        int width = 0;
        int consumedTotal = 0;
        while (!span.IsEmpty)
        {
            Rune.DecodeFromUtf16(span, out var rune, out int consumed);
            if (consumed <= 0)
            {
                break;
            }

            int runeWidth = UnicodeWidth.Width(rune);
            if (width + runeWidth > maxWidth)
            {
                break;
            }

            width += runeWidth;
            consumedTotal += consumed;
            span = span[consumed..];
        }

        return consumedTotal;
    }
}
