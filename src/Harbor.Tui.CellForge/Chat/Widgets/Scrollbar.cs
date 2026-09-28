using System.Text;
using Harbor.Ui.Framework.Rendering;

namespace Harbor.Tui.CellForge.Widgets;

/// <summary>
/// Visible scrollbar for <see cref="ScrollableViewport"/> ([PRIM3b], part of #288):
/// a 1-column track + proportional thumb painted as an overlay into the
/// rightmost column of the host rect. The thumb math follows ratatui
/// <c>scrollbar.rs</c> (round-half-up sizing/position, thumb clamped to
/// <c>[1 .. track]</c> and <c>start + length &lt;= track</c>, <c>█</c> thumb
/// on a <c>│</c> track), driven by the viewport's
/// <c>(TotalContent, Offset, ViewportH)</c> triple.
///
/// Two deliberate remaps (ratatui is a reference, not a byte-port):
/// <list type="bullet">
/// <item>position domain is the viewport's legal <c>[0 .. MaxOffset]</c>,
/// not ratatui's <c>[0 .. content-1]</c> — a literal port would strand the
/// thumb mid-track at the bottom whenever the viewport is tall, because
/// <see cref="ScrollableViewport"/> clamps every offset to
/// <c>MaxOffset</c>. Here the thumb always spans the full track from top
/// (offset 0) to bottom (offset <c>MaxOffset</c>);</item>
/// <item>when the content fits (<c>MaxOffset == 0</c>) the scrollbar hides
/// instead of rendering a full-track thumb (standard overlay scrollbar UX —
/// no chrome when there is nothing to scroll).</item>
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

    /// <summary>Thumb glyph — full block for max contrast in a single column (ratatui DOUBLE_VERTICAL thumb).</summary>
    public const char ThumbGlyph = '█';

    /// <summary>Track style — theme Border slot, like every other side rule.</summary>
    public static CellStyle TrackStyle => PanelChrome.BorderStyle;

    /// <summary>Thumb style — Muted (brighter than the Border track, quieter than Accent).</summary>
    public static CellStyle ThumbStyle => new(ChatPalette.Muted);

    /// <summary>
    /// True when a scrollbar is warranted: the content overflows the viewport
    /// (<c>ScrollableViewport.MaxOffsetFor(totalContent, viewportH) &gt; 0</c>).
    /// </summary>
    public static bool IsVisible(long totalContent, int viewportH) =>
        ScrollableViewport.MaxOffsetFor(totalContent, viewportH) > 0;

    /// <summary>
    /// Thumb window inside a <paramref name="trackH"/>-tall column, in track
    /// space: rows <c>[ThumbStart .. ThumbStart + ThumbLength)</c> are thumb,
    /// the rest is track, and <c>ThumbStart + ThumbLength + TrackEnd == trackH</c>.
    /// The thumb length is proportional to <c>viewportH / totalContent</c>
    /// (at least one row); its start glides from 0 (offset 0) to
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
    /// Paints the scrollbar for <paramref name="viewport"/> into the rightmost
    /// column of <paramref name="rect"/> (overlay — content underneath is
    /// covered, never shifted). Returns <c>true</c> when any cell was painted,
    /// <c>false</c> when hidden (content fits or degenerate rect/viewport) —
    /// in the hidden case the buffer is untouched.
    /// </summary>
    public static bool TryPaint(ScreenBuffer buffer, Rect rect, ScrollableViewport viewport)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        ArgumentNullException.ThrowIfNull(viewport);
        return TryPaint(buffer, rect, viewport.TotalContent, viewport.Offset, viewport.ViewportH);
    }

    /// <summary>
    /// Paints the scrollbar for an explicit <c>(totalContent, offset, viewportH)</c>
    /// triple into the rightmost column of <paramref name="rect"/>. Same hidden
    /// contract as <see cref="TryPaint(ScreenBuffer, Rect, ScrollableViewport)"/>.
    /// </summary>
    public static bool TryPaint(ScreenBuffer buffer, Rect rect, long totalContent, long offset, int viewportH)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        if (rect.Width <= 0 || rect.Height <= 0 || viewportH <= 0)
        {
            return false;
        }

        var (thumbStart, thumbLength, _) = ThumbGeometry(totalContent, offset, viewportH, rect.Height);
        if (thumbLength <= 0)
        {
            return false;
        }

        int x = rect.Right - 1;
        var trackStyle = TrackStyle;
        var thumbStyle = ThumbStyle;
        var track = new Rune(TrackGlyph);
        var thumb = new Rune(ThumbGlyph);
        int thumbEnd = thumbStart + thumbLength;
        bool any = false;
        for (int row = 0; row < rect.Height; row++)
        {
            bool isThumb = row >= thumbStart && row < thumbEnd;
            any |= buffer.SetRune(x, rect.Y + row, isThumb ? thumb : track, isThumb ? thumbStyle : trackStyle);
        }

        return any;
    }

    /// <summary>Integer divide rounding to nearest (half up); <paramref name="denominator"/> must be positive.</summary>
    private static long RoundDiv(long numerator, long denominator) =>
        (numerator + (denominator >> 1)) / denominator;
}
