namespace Harbor.Tui.CellForge.Rendering;

/// <summary>
/// Slim terminal-control trait (issue #1160, epic #1155): the ratatui-core
/// <c>Backend</c> trait (<c>backend.rs</c>) ported onto the
/// AnsiWriter/Capabilities boundary.
///
/// <para>
/// Ratatui splits the world into backends (separate crates, one ~10-method
/// trait: draw over a diff iterator, cursor hide/show/position, save/restore,
/// clear region, scroll-region up/down, flush) and everything widget-ish above
/// it. This interface is the same cut: a diff iterator goes IN (see
/// <c>DiffEngine.Flush</c> driving <c>AnsiWriter.MoveTo/SetStyle/PutRune</c>)
/// and nothing widget-shaped is named here — only cursor, erase, and
/// scroll-region primitives that serialize to escape bytes.
/// </para>
///
/// <para>
/// Cursor save/restore audit: <see cref="SaveCursor"/> / <see cref="RestoreCursor"/>
/// emit DECSC/DECRC (<c>ESC 7</c> / <c>ESC 8</c>) and never query the terminal
/// for the position (no DECRQM/DSR round-trip). Querying races the input parser
/// — ratatui lesson #2691 — so the position is write-only by design; callers
/// that need it back must track it themselves (see
/// <c>AnsiWriter.TrackedX/TrackedY</c>).
/// </para>
///
/// <para>
/// Scroll-region rule: region set/reset/scroll are trait methods. Do NOT inline
/// <c>CSI r</c> / <c>CSI S</c> / <c>CSI T</c> literals at call sites via
/// <c>Raw</c> — the audit moves every such insert behind this seam so the byte
/// shapes stay asserted in one place (<c>TerminalControlBackendTests</c>).
/// </para>
/// </summary>
public interface ITerminalControlBackend
{
    /// <summary>Hide cursor (DECTCEM reset, <c>CSI ?25l</c>).</summary>
    void HideCursor();

    /// <summary>Show cursor (DECTCEM set, <c>CSI ?25h</c>).</summary>
    void ShowCursor();

    /// <summary>Absolute cursor position, 0-based cell coordinates (CUP).</summary>
    void SetCursorPosition(int x, int y);

    /// <summary>
    /// Save cursor + origin state (DECSC, <c>ESC 7</c>). Emit-only: never
    /// queries the terminal (ratatui #2691 race lesson).
    /// </summary>
    void SaveCursor();

    /// <summary>
    /// Restore cursor + origin state (DECRC, <c>ESC 8</c>). Emit-only: never
    /// queries the terminal (ratatui #2691 race lesson).
    /// </summary>
    void RestoreCursor();

    /// <summary>Erase whole screen (ED 2). The pen position is unchanged.</summary>
    void ClearScreen();

    /// <summary>
    /// Set the scroll region to rows [<paramref name="topRow"/>,
    /// <paramref name="bottomRow"/>], 0-based inclusive (DECSTBM,
    /// <c>CSI top;bottom r</c>). Marks the tracked pen unknown: cursor motion
    /// inside a region is terminal-defined.
    /// </summary>
    void SetScrollRegion(int topRow, int bottomRow);

    /// <summary>Reset the scroll region to the full screen (<c>CSI r</c>).</summary>
    void ResetScrollRegion();

    /// <summary>Scroll the scroll region up by <paramref name="lines"/> (SU, <c>CSI n S</c>).</summary>
    void ScrollRegionUp(int lines);

    /// <summary>Scroll the scroll region down by <paramref name="lines"/> (SD, <c>CSI n T</c>).</summary>
    void ScrollRegionDown(int lines);
}
