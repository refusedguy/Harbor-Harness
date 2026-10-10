using System.Text;
using Harbor.Tui.CellForge.Rendering;

namespace Harbor.Tui.CellForge.Widgets;

/// <summary>
/// Which frame <see cref="PanelChrome.PaintBorderBox"/> draws. Two dialects
/// exist in the chat UI and both are deliberate: modals and pickers speak
/// rounded, the fullscreen image viewer speaks rectilinear.
/// </summary>
public enum BoxStyle : byte
{
    /// <summary>
    /// The dialog language: rounded <c>╭─╮ / │ │ / ╰─╯</c> over a blanked
    /// interior. What every overlay, picker and palette frame uses.
    /// </summary>
    RoundedPanel = 0,

    /// <summary>
    /// The image viewer's rectilinear frame <c>┌─┐ / │ │ / └─┘</c>, drawn over
    /// an interior the CALLER already blanked. The viewer is opaque and
    /// fullscreen: it wipes its own surface first (so no feed cell survives
    /// under the picture), and a second fill here would only repaint blanks
    /// with blanks. Keeping it a parameter rather than a private copy is what
    /// stops the seventh box painter from being born again.
    /// </summary>
    SquareFrame = 1,
}

/// <summary>
/// Panel chrome for the CellForge chat screen: inter-panel borders, panel
/// title rows, chrome backgrounds and inter-message separators (prod-testing
/// feedback: the screen was a single monochrome canvas with no visible zones).
///
/// Single source of truth for chrome STYLES — every color comes from an
/// existing <see cref="ChatPalette"/> / <see cref="TerminalColorPalette"/>
/// slot, never a hardcoded hex or <c>PackedColor.Indexed</c> literal:
/// <list type="bullet">
/// <item>borders / rules → <see cref="ChatPalette.Border"/> (theme Border slot);</item>
/// <item>titles → <see cref="ChatPalette.Accent"/> + bold (same tone as the
/// sidebar SECTION headings in <see cref="SideBarView"/>);</item>
/// <item>chrome backgrounds → <see cref="ChatPalette.Panel"/> /
/// <see cref="ChatPalette.Surface"/> (theme Panel/Surface slots);</item>
/// <item>message separators → <see cref="ChatPalette.Dim"/> (thin dim line).</item>
/// </list>
/// The chat feed itself keeps the default background as the reference surface —
/// panels read darker/lighter against it, so the eye catches the zones.
///
/// All painters are bounds-safe (clip to the buffer, never throw on tiny or
/// empty rects) and allocation-free on steady-state frames.
///
/// <see cref="PaintBorderBox"/> is the only frame painter for overlays in
/// this layer: the seven private copies it replaced in #553 were the
/// nearest-existing-code trap — whichever one you copied decided whether
/// the degenerate-rect guard came with it. The frame <c>Panel</c> widgets
/// (<c>BorderKind</c>/<c>BorderPanel</c>, in <c>Chat/Rendering/LayoutTree.cs</c>
/// since the #436 move out of the engine) are a deliberately separate owner:
/// four dialects plus title/shadow/focus semantics that overlays do not
/// share — so a new overlay still calls <c>PaintBorderBox</c> instead.
/// </summary>
public static class PanelChrome
{
    /// <summary>Inter-panel border / rule glyph style — theme Border slot.</summary>
    public static CellStyle BorderStyle => new(ChatPalette.Border);

    /// <summary>Panel title style — accent + bold (sidebar SECTION pattern).</summary>
    public static CellStyle TitleStyle => new(ChatPalette.Accent, attrs: StyleAttr.Bold);

    /// <summary>Chrome row background — theme Panel slot.</summary>
    public static PackedColor PanelBackground => ChatPalette.Panel;

    /// <summary>Alternate chrome background — theme Surface slot.</summary>
    public static PackedColor SurfaceBackground => ChatPalette.Surface;

    /// <summary>Inter-message separator style — thin dim line.</summary>
    public static CellStyle SeparatorStyle => ChatPalette.Dim;

    /// <summary>Composer panel title (painted by <see cref="ComposerPanel"/> when chromed).</summary>
    public const string ComposerTitle = "INPUT";

    /// <summary>Sidebar panel title (painted by <see cref="SideBarView"/>).</summary>
    public const string SidebarTitle = "CONTEXT";

    /// <summary>Horizontal rule glyph used for top/bottom borders and separators.</summary>
    public const char RuleGlyph = '─';

    /// <summary>Vertical rule glyph used for side borders.</summary>
    public const char SideGlyph = '│';

    /// <summary>
    /// The four corner glyphs of <paramref name="style"/> in paint order —
    /// top-left, top-right, bottom-left, bottom-right. The overlay-frame
    /// glyph table (layout frames keep their own in <c>BorderPanel</c>).
    /// </summary>
    public static ReadOnlySpan<char> CornersFor(BoxStyle style) => style == BoxStyle.SquareFrame
        ? "┌┐└┘"
        : "╭╮╰╯";

    /// <summary>
    /// Paints a horizontal <c>─</c> rule across the top row of
    /// <paramref name="rect"/> in <see cref="BorderStyle"/>. No-op on empty rects.
    /// </summary>
    public static void PaintTopRule(ScreenBuffer buffer, Rect rect)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        PaintRule(buffer, rect.X, rect.Y, rect.Width);
    }

    /// <summary>
    /// Paints a horizontal <c>─</c> rule across the bottom row of
    /// <paramref name="rect"/> in <see cref="BorderStyle"/>. No-op on empty rects.
    /// </summary>
    public static void PaintBottomRule(ScreenBuffer buffer, Rect rect)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        if (rect.Height <= 0)
        {
            return;
        }

        PaintRule(buffer, rect.X, rect.Bottom - 1, rect.Width);
    }

    /// <summary>
    /// Paints a vertical <c>│</c> rule down the left column of
    /// <paramref name="rect"/> in <see cref="BorderStyle"/>. No-op on empty rects.
    /// </summary>
    public static void PaintLeftRule(ScreenBuffer buffer, Rect rect)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        if (rect.Width <= 0 || rect.Height <= 0)
        {
            return;
        }

        var style = BorderStyle;
        var glyph = new Rune(SideGlyph);
        for (int i = 0; i < rect.Height; i++)
        {
            buffer.SetRune(rect.X, rect.Y + i, glyph, in style);
        }
    }

    /// <summary>
    /// Paints a titled rule row at (<paramref name="x"/>, <paramref name="y"/>):
    /// <c>─ TITLE ───…</c> — the title in <see cref="TitleStyle"/>, the rules in
    /// <see cref="BorderStyle"/>. Truncates the title when narrower than it;
    /// no-op when <paramref name="width"/> is not positive.
    /// </summary>
    public static void PaintTitleRow(ScreenBuffer buffer, int x, int y, int width, string title)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        if (width <= 0 || string.IsNullOrEmpty(title))
        {
            return;
        }

        var ruleStyle = BorderStyle;
        var titleStyle = TitleStyle;
        var rule = new Rune(RuleGlyph);

        // Leading rule + space …
        buffer.SetRune(x, y, rule, in ruleStyle);
        buffer.SetRune(x + 1, y, new Rune(' '), in ruleStyle);

        // … title (truncated to the available width) …
        int titleAvail = Math.Max(0, width - 3);
        var text = title.AsSpan(0, Math.Min(title.Length, titleAvail));
        buffer.SetText(x + 2, y, text, in titleStyle);

        // … trailing rule to the right edge.
        int fillFrom = x + 2 + text.Length + 1;
        int fillTo = x + width;
        for (int cx = fillFrom; cx < fillTo; cx++)
        {
            buffer.SetRune(cx, y, rule, in ruleStyle);
        }

        // Gap between title and trailing rule.
        if (fillFrom < fillTo)
        {
            buffer.SetRune(fillFrom - 1, y, new Rune(' '), in ruleStyle);
        }
    }

    /// <summary>
    /// Paints an inter-message separator: a thin dim <c>─</c> line across
    /// (<paramref name="x"/>, <paramref name="y"/>, <paramref name="width"/>)
    /// in <see cref="SeparatorStyle"/>. No-op when <paramref name="width"/>
    /// is not positive. Message blocks call this in their trailing gap row when
    /// <see cref="BlockPaintContext.ShowSeparators"/> is set, so answers stop
    /// blending into each other.
    /// </summary>
    public static void PaintMessageSeparator(ScreenBuffer buffer, int x, int y, int width)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        if (width <= 0)
        {
            return;
        }

        var style = SeparatorStyle;
        var glyph = new Rune(RuleGlyph);
        for (int i = 0; i < width; i++)
        {
            buffer.SetRune(x + i, y, glyph, in style);
        }
    }

    /// <summary>
    /// Fills <paramref name="rect"/> with blank cells on the
    /// <see cref="PanelBackground"/> surface. No-op on empty rects.
    /// </summary>
    public static void FillPanelBackground(ScreenBuffer buffer, Rect rect)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        if (rect.Width <= 0 || rect.Height <= 0)
        {
            return;
        }

        var cell = Cell.From(new Rune(' '), new CellStyle(bg: PanelBackground));
        buffer.Fill(rect, in cell);
    }

    /// <summary>
    /// Paints the panel box <paramref name="rect"/>: <see cref="BoxStyle.RoundedPanel"/>
    /// blanks the interior and frames it with the rounded <c>╭╮╰╯</c> set in
    /// <see cref="BorderStyle"/>. Single owner of the dialog frame since #553 —
    /// seven overlays used to hand-roll this method and two of those copies
    /// had lost the degenerate-rect guard below, painting corner glyphs
    /// OUTSIDE the requested rect when it was narrower than two columns (a
    /// squeezed pane or a split-pane drag scribbled into the neighbouring
    /// cell). The guard is now unconditional and lives here, once.
    ///
    /// Bounds-safe twice over: a rect with no room for an interior (below
    /// 2×2) paints nothing at all, and every write clips to the buffer.
    /// Allocation-free on steady-state frames.
    /// </summary>
    /// <param name="buffer">Target grid; <see langword="null"/> throws.</param>
    /// <param name="rect">The box to paint; may be degenerate or off-buffer.</param>
    /// <param name="style">Frame dialect; see <see cref="BoxStyle"/>.</param>
    public static void PaintBorderBox(ScreenBuffer buffer, Rect rect, BoxStyle style = BoxStyle.RoundedPanel)
    {
        ArgumentNullException.ThrowIfNull(buffer);

        if (style == BoxStyle.RoundedPanel)
        {
            FillBoxInterior(buffer, rect);
        }

        // A frame needs one cell on each side of an interior: below 2×2 there
        // is no interior, and x2/y2 already sit outside the rect — so the
        // corner writes would land in the neighbouring cell. Return FIRST,
        // before any corner is written.
        if (rect.Width < 2 || rect.Height < 2)
        {
            return;
        }

        int x1 = rect.X, y1 = rect.Y, x2 = rect.Right - 1, y2 = rect.Bottom - 1;
        ReadOnlySpan<char> corners = CornersFor(style);
        var frame = BorderStyle;

        // SetRune (not the unchecked At(x, y) cell indexer the old copies used)
        // clips to the buffer, so a box hanging off the edge paints what fits
        // instead of writing into the neighbouring row.
        buffer.SetRune(x1, y1, new Rune(corners[0]), in frame);
        buffer.SetRune(x2, y1, new Rune(corners[1]), in frame);
        buffer.SetRune(x1, y2, new Rune(corners[2]), in frame);
        buffer.SetRune(x2, y2, new Rune(corners[3]), in frame);

        var rule = new Rune(RuleGlyph);
        for (int x = x1 + 1; x < x2; x++)
        {
            buffer.SetRune(x, y1, rule, in frame);
            buffer.SetRune(x, y2, rule, in frame);
        }

        var side = new Rune(SideGlyph);
        for (int y = y1 + 1; y < y2; y++)
        {
            buffer.SetRune(x1, y, side, in frame);
            buffer.SetRune(x2, y, side, in frame);
        }
    }

    /// <summary>
    /// Blanks a box interior so no chat cell survives under a frame. The panel
    /// tone rides the FOREGROUND slot of a space cell — inert on screen, and
    /// byte-for-byte what the seven hand-rolled box painters wrote. Moving it
    /// to the background slot (what <see cref="FillPanelBackground"/> does for
    /// chrome rows) would tint every modal interior, so the quirk lives here
    /// once instead of at seven call sites.
    /// </summary>
    private static void FillBoxInterior(ScreenBuffer buffer, Rect rect)
    {
        if (rect.Width <= 0 || rect.Height <= 0)
        {
            return;
        }

        var cell = Cell.From(new Rune(' '), new CellStyle(PanelBackground));
        buffer.Fill(rect, in cell);
    }

    private static void PaintRule(ScreenBuffer buffer, int x, int y, int width)
    {
        if (width <= 0)
        {
            return;
        }

        var style = BorderStyle;
        var glyph = new Rune(RuleGlyph);
        for (int i = 0; i < width; i++)
        {
            buffer.SetRune(x + i, y, glyph, in style);
        }
    }
}
