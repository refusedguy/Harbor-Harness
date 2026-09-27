using System.Text;
using Harbor.Tui.CellForge.Rendering;

namespace Harbor.Tui.CellForge.Widgets;

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
