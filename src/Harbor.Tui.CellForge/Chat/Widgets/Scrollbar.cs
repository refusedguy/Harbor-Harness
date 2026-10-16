using System.Text;
using Ensure = Harbor.Abstractions.Extensions.Ensure;
using Harbor.Ui.Framework.Rendering;

namespace Harbor.Tui.CellForge.Widgets;

/// <summary>
/// Position of a <see cref="Scrollbar"/> around its host rect (ratatui
/// <c>scrollbar.rs: ScrollbarOrientation</c> parity).
/// </summary>
public enum ScrollbarOrientation
{
    /// <summary>Right column, scrolling vertically (default; the only mode [PRIM3b] painted).</summary>
    VerticalRight,

    /// <summary>Left column, scrolling vertically.</summary>
    VerticalLeft,

    /// <summary>Bottom row, scrolling horizontally.</summary>
    HorizontalBottom,

    /// <summary>Top row, scrolling horizontally.</summary>
    HorizontalTop,
}

/// <summary>Orientation helpers (ratatui <c>ScrollbarOrientation::is_vertical/is_horizontal</c> parity).</summary>
public static class ScrollbarOrientationExtensions
{
    /// <summary>True for <see cref="ScrollbarOrientation.VerticalRight"/>/<see cref="ScrollbarOrientation.VerticalLeft"/>.</summary>
    public static bool IsVertical(this ScrollbarOrientation orientation) =>
        orientation is ScrollbarOrientation.VerticalRight or ScrollbarOrientation.VerticalLeft;

    /// <summary>True for <see cref="ScrollbarOrientation.HorizontalBottom"/>/<see cref="ScrollbarOrientation.HorizontalTop"/>.</summary>
    public static bool IsHorizontal(this ScrollbarOrientation orientation) => !IsVertical(orientation);
}

/// <summary>
/// Scroll step direction (ratatui <c>scrollbar.rs: ScrollDirection</c> parity).
/// Forward = down/right, Backward = up/left.
/// </summary>
public enum ScrollDirection
{
    /// <summary>Forward scroll — down or right.</summary>
    Forward,

    /// <summary>Backward scroll — up or left.</summary>
    Backward,
}

/// <summary>
/// <see cref="ScrollDirection"/> helpers over the scrollbar state authority,
/// <see cref="ScrollableViewport"/> (ratatui <c>ScrollbarState::scroll/prev/next</c> parity —
/// <c>first</c>/<c>last</c> map to <c>ScrollToTop</c>/<c>ScrollToEnd</c>,
/// <c>is_at_start</c>/<c>is_at_end</c> map to <c>AtTop</c>/<c>AtBottom</c>, so no
/// parallel state type is introduced).
/// </summary>
public static class ScrollableViewportScrollbarExtensions
{
    /// <summary>
    /// Moves <paramref name="viewport"/> by <paramref name="amount"/> rows in
    /// <paramref name="direction"/> (clamped). Returns the clamped offset.
    /// </summary>
    public static long Scroll(
        this ScrollableViewport viewport,
        ScrollDirection direction,
        long amount = 1)
    {
        Ensure.NotNull(viewport);
        return direction == ScrollDirection.Forward
            ? viewport.ScrollBy(amount)
            : viewport.ScrollBy(-amount);
    }
}

/// <summary>
/// Glyph set for a <see cref="Scrollbar"/> (ratatui
/// <c>symbols::scrollbar::Set</c> parity: track/thumb/begin/end).
/// All glyphs are single-cell; begin/end are <c>null</c> when the scrollbar
/// has no arrow heads (the Harbor default — overlay chrome stays byte-identical
/// until a host opts in).
/// </summary>
public readonly record struct ScrollbarSymbols(char Track, char Thumb, char? Begin, char? End)
{
    /// <summary>
    /// Harbor default: single-line track, full-block thumb, no arrow heads.
    /// Matches the [PRIM3b] paint exactly (<c>│</c>/<c>█</c>).
    /// </summary>
    public static ScrollbarSymbols Default => new('│', '█', null, null);

    /// <summary>Ratatui <c>DOUBLE_VERTICAL</c>: <c>║</c>/<c>█</c>/<c>▲</c>/<c>▼</c>.</summary>
    public static ScrollbarSymbols DoubleVertical => new('║', '█', '▲', '▼');

    /// <summary>Ratatui <c>DOUBLE_HORIZONTAL</c>: <c>═</c>/<c>█</c>/<c>◄</c>/<c>►</c>.</summary>
    public static ScrollbarSymbols DoubleHorizontal => new('═', '█', '◄', '►');

    /// <summary>Ratatui <c>VERTICAL</c>: <c>│</c>/<c>█</c>/<c>↑</c>/<c>↓</c>.</summary>
    public static ScrollbarSymbols SingleVertical => new('│', '█', '↑', '↓');

    /// <summary>Ratatui <c>HORIZONTAL</c>: <c>─</c>/<c>█</c>/<c>←</c>/<c>→</c>.</summary>
    public static ScrollbarSymbols SingleHorizontal => new('─', '█', '←', '→');
}

/// <summary>
/// Visible scrollbar for <see cref="ScrollableViewport"/> ([PRIM3b], part of #288):
/// a 1-cell track + proportional thumb painted as an overlay into the
/// edge row/column of the host rect. The thumb math follows ratatui
/// <c>scrollbar.rs</c> (round-half-up sizing/position, thumb clamped to
/// <c>[1 .. track]</c> and <c>start + length &lt;= track</c>, <c>█</c> thumb
/// on a <c>│</c> track), driven by the viewport's
/// <c>(TotalContent, Offset, ViewportH)</c> triple.
/// Orientation, symbol sets and split begin/end styles follow ratatui
/// <c>ScrollbarOrientation</c> / <c>symbols::scrollbar::{DOUBLE_VERTICAL,
/// DOUBLE_HORIZONTAL, VERTICAL, HORIZONTAL}</c> / per-part styles (#1164).
///
/// Three deliberate remaps (ratatui is a reference, not a byte-port):
/// <list type="bullet">
/// <item>position domain is the viewport's legal <c>[0 .. MaxOffset]</c>,
/// not ratatui's <c>[0 .. content-1]</c> — a literal port would strand the
/// thumb mid-track at the bottom whenever the viewport is tall, because
/// <see cref="ScrollableViewport"/> clamps every offset to
/// <c>MaxOffset</c>. Here the thumb always spans the full track from top
/// (offset 0) to bottom (offset <c>MaxOffset</c>);</item>
/// <item>when the content fits (<c>MaxOffset == 0</c>) the scrollbar hides
/// instead of rendering a full-track thumb (standard overlay scrollbar UX —
/// no chrome when there is nothing to scroll);</item>
/// <item>no parallel <c>ScrollbarState</c> type: <see cref="ScrollableViewport"/>
/// already owns the <c>(content_length, position, viewport_content_length)</c>
/// triple, so only <see cref="ScrollDirection"/> plus the
/// <see cref="ScrollableViewportScrollbarExtensions.Scroll"/> helper are ported.</item>
/// </list>
///
/// Render only: owns no scroll state (the <see cref="ScrollableViewport"/>
/// is the authority), touches no palette slots beyond the existing
/// <see cref="ChatPalette"/> entries, and is never wired into the timeline,
/// palette, blocks or layout — hosts opt in by calling
/// <see cref="TryPaint"/> after their own paint. Hidden (paints nothing,
/// returns <c>false</c>) when the content fits or the rect is degenerate, so
/// existing goldens stay byte-identical until a host enables it. Bounds-safe
/// (clips via <c>ScreenBuffer.SetRune</c>), allocation-free and AOT-safe.</summary>
public static class Scrollbar
{
    /// <summary>Track glyph — same side rule as panel borders (<see cref="PanelChrome.SideGlyph"/>).</summary>
    public const char TrackGlyph = '│';

    /// <summary>Thumb glyph — full block for max contrast in a single cell (ratatui DOUBLE_VERTICAL thumb).</summary>
    public const char ThumbGlyph = '█';

    /// <summary>Track style — theme Border slot, like every other side rule.</summary>
    public static CellStyle TrackStyle => PanelChrome.BorderStyle;

    /// <summary>Thumb style — Muted (brighter than the Border track, quieter than Accent).</summary>
    public static CellStyle ThumbStyle => new(ChatPalette.Muted);

    /// <summary>Begin-arrow style — Border slot, like the track (ratatui per-part style parity, overridable).</summary>
    public static CellStyle BeginStyle => PanelChrome.BorderStyle;

    /// <summary>End-arrow style — Border slot, like the track (ratatui per-part style parity, overridable).</summary>
    public static CellStyle EndStyle => PanelChrome.BorderStyle;

    /// <summary>
    /// True when a scrollbar is warranted: the content overflows the viewport
    /// (<c>ScrollableViewport.MaxOffsetFor(totalContent, viewportH) &gt; 0</c>).
    /// Detachable overload: any widget's <see cref="ScrollInfo"/> triple.
    /// </summary>
    public static bool IsVisible(ScrollInfo info) =>
        IsVisible(info.Total, info.Viewport);

    /// <summary>
    /// True when a scrollbar is warranted: the content overflows the viewport
    /// (<c>ScrollableViewport.MaxOffsetFor(totalContent, viewportH) &gt; 0</c>).
    /// </summary>
    public static bool IsVisible(long totalContent, int viewportH) =>
        ScrollableViewport.MaxOffsetFor(totalContent, viewportH) > 0;

    /// <summary>
    /// Thumb window for a detachable <see cref="ScrollInfo"/> triple.
    /// Same math as the <c>(total, offset, viewport)</c> overload.
    /// </summary>
    public static (int ThumbStart, int ThumbLength, int TrackEnd) ThumbGeometry(
        ScrollInfo info, int trackH) =>
        ThumbGeometry(info.Total, info.Offset, info.Viewport, trackH);

    /// <summary>
    /// Thumb window inside a <paramref name="trackH"/>-long track, in track
    /// space: cells <c>[ThumbStart .. ThumbStart + ThumbLength)</c> are thumb,
    /// the rest is track, and <c>ThumbStart + ThumbLength + TrackEnd == trackH</c>.
    /// <paramref name="trackH"/> is the track length along the paint axis
    /// (column height for vertical, row width for horizontal orientations —
    /// pass <see cref="TrackLengthExcludingEnds"/> when arrow heads are painted).
    /// The thumb length is proportional to <c>viewportH / totalContent</c>
    /// (at least one cell); its start glides from 0 (offset 0) to
    /// <c>trackH - ThumbLength</c> (offset <c>MaxOffset</c>). Returns
    /// <c>(0, 0, 0)</c> when hidden (content fits/empty or degenerate
    /// geometry). The offset is clamped to the legal range first, so callers
    /// may pass a stale value. Pure and allocation-free.
    /// </summary>
    public static (int ThumbStart, int ThumbLength, int TrackEnd) ThumbGeometry(
        long totalContent, long offset, int viewportH, int trackH)
    {
        if (trackH <= 0 || viewportH <= 0 || totalContent <= 0)
        {
            return (0, 0, 0);
        }

        long maxOffset = ScrollableViewport.MaxOffsetFor(totalContent, viewportH);
        if (maxOffset <= 0)
        {
            return (0, 0, 0);
        }

        long clamped = Math.Clamp(offset, 0, maxOffset);
        long track = trackH;

        int thumbLength = (int)Math.Clamp(RoundDiv((long)viewportH * track, totalContent), 1, track);
        int thumbStart = (int)Math.Clamp(RoundDiv(clamped * (track - thumbLength), maxOffset), 0, track - thumbLength);
        int trackEnd = trackH - thumbStart - thumbLength;
        return (thumbStart, thumbLength, trackEnd);
    }

    /// <summary>
    /// Track length available to the thumb once arrow heads are reserved
    /// (ratatui <c>track_length_excluding_arrow_heads</c> parity — every
    /// symbol is one cell, so each present head costs one cell, floored at 0).
    /// </summary>
    public static int TrackLengthExcludingEnds(int trackLen, ScrollbarSymbols symbols) =>
        Math.Max(0, trackLen - (symbols.Begin.HasValue ? 1 : 0) - (symbols.End.HasValue ? 1 : 0));

    /// <summary>
    /// Paints the scrollbar for <paramref name="viewport"/> as an overlay on
    /// the edge of <paramref name="rect"/> (content underneath is covered,
    /// never shifted): the right/left column for vertical orientations, the
    /// bottom/top row for horizontal ones. Returns <c>true</c> when any cell
    /// was painted, <c>false</c> when hidden (content fits or degenerate
    /// rect/viewport) — in the hidden case the buffer is untouched.
    /// Defaults (vertical-right, <see cref="ScrollbarSymbols.Default"/>, no
    /// arrow heads) paint exactly like [PRIM3b].
    /// </summary>
    public static bool TryPaint(
        ScreenBuffer buffer,
        Rect rect,
        ScrollableViewport viewport,
        ScrollbarOrientation orientation = ScrollbarOrientation.VerticalRight,
        ScrollbarSymbols? symbols = null,
        CellStyle? trackStyle = null,
        CellStyle? thumbStyle = null,
        CellStyle? beginStyle = null,
        CellStyle? endStyle = null)
    {
        Ensure.NotNull(buffer);
        Ensure.NotNull(viewport);
        int viewportLen = orientation.IsVertical() ? viewport.ViewportH : rect.Width;
        return TryPaint(
            buffer, rect, viewport.TotalContent, viewport.Offset, viewportLen,
            orientation, symbols, trackStyle, thumbStyle, beginStyle, endStyle);
    }

    /// <summary>
    /// Paints the scrollbar for a detachable <see cref="ScrollInfo"/> triple
    /// into the rightmost column of <paramref name="rect"/> (#1206: any
    /// scrollable widget yields the triple, this shared indicator renders on
    /// top, no scroll ownership). Same hidden contract as the
    /// <c>(totalContent, offset, viewportH)</c> overload.
    /// </summary>
    public static bool TryPaint(ScreenBuffer buffer, Rect rect, ScrollInfo info)
    {
        return TryPaint(buffer, rect, info.Total, info.Offset, info.Viewport);
    }

    /// <summary>
    /// Paints the scrollbar for an explicit <c>(totalContent, offset, viewportH)</c>
    /// triple as an overlay on the edge of <paramref name="rect"/>. Same hidden
    /// contract as the <see cref="TryPaint(ScreenBuffer, Rect, ScrollableViewport)"/>
    /// overload.
    /// For horizontal orientations <paramref name="viewportH"/> is the visible
    /// length along the scroll axis (pass the host width; ratatui falls back to
    /// the track width when the viewport length is unset).
    /// </summary>
    public static bool TryPaint(
        ScreenBuffer buffer,
        Rect rect,
        long totalContent,
        long offset,
        int viewportH,
        ScrollbarOrientation orientation = ScrollbarOrientation.VerticalRight,
        ScrollbarSymbols? symbols = null,
        CellStyle? trackStyle = null,
        CellStyle? thumbStyle = null,
        CellStyle? beginStyle = null,
        CellStyle? endStyle = null)
    {
        Ensure.NotNull(buffer);
        if (rect.Width <= 0 || rect.Height <= 0 || viewportH <= 0)
        {
            return false;
        }

        var set = symbols ?? ScrollbarSymbols.Default;
        var track = new Rune(set.Track);
        var thumb = new Rune(set.Thumb);
        var resolvedTrack = trackStyle ?? TrackStyle;
        var resolvedThumb = thumbStyle ?? ThumbStyle;
        var resolvedBegin = beginStyle ?? BeginStyle;
        var resolvedEnd = endStyle ?? EndStyle;

        int trackLen = orientation.IsVertical() ? rect.Height : rect.Width;
        int innerLen = TrackLengthExcludingEnds(trackLen, set);
        int headOffset = set.Begin.HasValue ? 1 : 0;

        if (innerLen <= 0)
        {
            // Degenerate track: only arrow heads can fit (ratatui renders
            // begin/end with a zero-length track the same way).
            return PaintHeadsOnly(buffer, rect, orientation, set, resolvedBegin, resolvedEnd);
        }

        var (thumbStart, thumbLength, _) = ThumbGeometry(totalContent, offset, viewportH, innerLen);
        if (thumbLength <= 0)
        {
            return false;
        }

        int thumbEnd = headOffset + thumbStart + thumbLength;
        bool any = false;

        if (set.Begin.HasValue)
        {
            any |= PaintCell(buffer, rect, orientation, 0, new Rune(set.Begin.Value), resolvedBegin);
        }

        for (int i = 0; i < innerLen; i++)
        {
            int pos = headOffset + i;
            bool isThumb = pos >= headOffset + thumbStart && pos < thumbEnd;
            any |= PaintCell(
                buffer, rect, orientation, pos,
                isThumb ? thumb : track,
                isThumb ? resolvedThumb : resolvedTrack);
        }

        if (set.End.HasValue)
        {
            any |= PaintCell(buffer, rect, orientation, trackLen - 1, new Rune(set.End.Value), resolvedEnd);
        }

        return any;
    }

    private static bool PaintHeadsOnly(
        ScreenBuffer buffer,
        Rect rect,
        ScrollbarOrientation orientation,
        ScrollbarSymbols set,
        CellStyle resolvedBegin,
        CellStyle resolvedEnd)
    {
        int trackLen = orientation.IsVertical() ? rect.Height : rect.Width;
        if (trackLen <= 0)
        {
            return false;
        }

        bool any = false;
        if (set.Begin.HasValue)
        {
            any |= PaintCell(buffer, rect, orientation, 0, new Rune(set.Begin.Value), resolvedBegin);
        }

        if (set.End.HasValue && trackLen >= 2)
        {
            any |= PaintCell(buffer, rect, orientation, trackLen - 1, new Rune(set.End.Value), resolvedEnd);
        }

        return any;
    }

    private static bool PaintCell(
        ScreenBuffer buffer,
        Rect rect,
        ScrollbarOrientation orientation,
        int pos,
        Rune rune,
        CellStyle style) => orientation switch
    {
        ScrollbarOrientation.VerticalRight => buffer.SetRune(rect.Right - 1, rect.Y + pos, rune, style),
        ScrollbarOrientation.VerticalLeft => buffer.SetRune(rect.X, rect.Y + pos, rune, style),
        ScrollbarOrientation.HorizontalBottom => buffer.SetRune(rect.X + pos, rect.Bottom - 1, rune, style),
        ScrollbarOrientation.HorizontalTop => buffer.SetRune(rect.X + pos, rect.Y, rune, style),
        _ => buffer.SetRune(rect.Right - 1, rect.Y + pos, rune, style),
    };

    /// <summary>Integer divide rounding to nearest (half up); <paramref name="denominator"/> must be positive.</summary>
    private static long RoundDiv(long numerator, long denominator) =>
        (numerator + (denominator >> 1)) / denominator;
}
