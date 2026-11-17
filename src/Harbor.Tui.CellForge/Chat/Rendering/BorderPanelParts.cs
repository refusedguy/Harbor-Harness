using System.Text;
using UIR = Harbor.Ui.Framework.Rendering;

namespace Harbor.Tui.CellForge.Rendering;

/// <summary>
/// The single style source for the typed parts of <see cref="BorderPanel"/>
/// (#2355). Each part resolves its own <see cref="UIR.CellStyle"/> from its
/// own state by picking one of these values; the panel root never passes a
/// style into a part and no part hardcodes a color. Same discipline as
/// gpui-kit's per-part <c>semantic_styles</c> resolving from the shared theme.
/// </summary>
internal static class BorderPanelStyles
{
    /// <summary>Unfocused frame / title style — theme Border slot.</summary>
    internal static readonly UIR.CellStyle Frame = new(UIR.PackedColor.Indexed(8));

    /// <summary>Focused frame / title style — the focus ring is bold.</summary>
    internal static readonly UIR.CellStyle Focused = new(attrs: UIR.StyleAttr.Bold);

    /// <summary>One-cell drop shadow style — theme Border slot as background.</summary>
    internal static readonly UIR.CellStyle Shadow = new(bg: UIR.PackedColor.Indexed(8));
}

/// <summary>
/// Typed frame part of <see cref="BorderPanel"/>. Owns its own visual state
/// (<see cref="Kind"/> and <see cref="Focused"/>), resolves its own glyph table
/// and <see cref="Style"/> from that state, and paints itself inside the one
/// rect the panel root hands to <see cref="Paint"/>. The root never walks a
/// part tree by name and never threads a style flag down: this is the
/// gpui-kit <c>CheckboxIndicator</c> discipline applied to a CellForge
/// composite (#2355). Geometry has one owner — the root's
/// <see cref="Panel.Rect"/> — so this part only derives paint coordinates.
/// </summary>
internal sealed class BorderFramePart
{
    public BorderFramePart(BorderKind kind = BorderKind.Plain, bool focused = false)
    {
        Kind = kind;
        Focused = focused;
    }

    /// <summary>Box glyph set this part owns and renders.</summary>
    public BorderKind Kind { get; set; }

    /// <summary>Focus state this part uses to resolve its own style.</summary>
    public bool Focused { get; set; }

    /// <summary>The style this part resolved from its own focus state.</summary>
    public UIR.CellStyle Style => Focused ? BorderPanelStyles.Focused : BorderPanelStyles.Frame;

    /// <summary>
    /// Glyph table for <see cref="Kind"/>: top-left, top-right, bottom-left,
    /// bottom-right, horizontal, vertical (the order <see cref="Paint"/> uses).
    /// A value tuple, so resolving the part's glyphs allocates nothing.
    /// </summary>
    public (char TL, char TR, char BL, char BR, char H, char V) Glyphs => Kind switch
    {
        BorderKind.Rounded => ('╭', '╮', '╰', '╯', '─', '│'),
        BorderKind.Double => ('╔', '╗', '╚', '╝', '═', '║'),
        BorderKind.Thick => ('┏', '┓', '┗', '┛', '━', '┃'),
        _ => ('┌', '┐', '└', '┘', '─', '│'),
    };

    /// <summary>
    /// Paints the frame inside <paramref name="rect"/>. The caller (the panel
    /// root) owns the rect and guards it to at least 2x2; this part only reads
    /// it. Byte-identical to the former inline painter in <c>BorderPanel</c>.
    /// </summary>
    public void Paint(UIR.ScreenBuffer buffer, UIR.Rect rect)
    {
        var (tl, tr, bl, br, h, v) = Glyphs;
        var style = Style;

        var topLeft = UIR.Cell.From(new Rune(tl), style);
        var topRight = UIR.Cell.From(new Rune(tr), style);
        var bottomLeft = UIR.Cell.From(new Rune(bl), style);
        var bottomRight = UIR.Cell.From(new Rune(br), style);
        var horiz = UIR.Cell.From(new Rune(h), style);
        var vert = UIR.Cell.From(new Rune(v), style);

        int x1 = rect.X, y1 = rect.Y, x2 = rect.Right - 1, y2 = rect.Bottom - 1;

        buffer.At(x1, y1) = topLeft;
        buffer.At(x2, y1) = topRight;
        buffer.At(x1, y2) = bottomLeft;
        buffer.At(x2, y2) = bottomRight;

        for (int x = x1 + 1; x < x2; x++)
        {
            buffer.At(x, y1) = horiz;
            buffer.At(x, y2) = horiz;
        }

        for (int y = y1 + 1; y < y2; y++)
        {
            buffer.At(x1, y) = vert;
            buffer.At(x2, y) = vert;
        }
    }
}

/// <summary>
/// Typed title part of <see cref="BorderPanel"/>. Owns the title text and its
/// own focus-driven style; the root no longer reaches into a painter to set
/// the title's color (#2355).
/// </summary>
internal sealed class BorderTitlePart
{
    public BorderTitlePart(string text = "", bool focused = false)
    {
        Text = text;
        Focused = focused;
    }

    /// <summary>Title text rendered on the top border.</summary>
    public string Text { get; set; }

    /// <summary>Focus state this part uses to resolve its own style.</summary>
    public bool Focused { get; set; }

    /// <summary>The style this part resolved from its own focus state.</summary>
    public UIR.CellStyle Style => Focused ? BorderPanelStyles.Focused : BorderPanelStyles.Frame;

    /// <summary>
    /// Paints the title on the top row of <paramref name="rect"/> when it fits.
    /// Guards are the former inline ones, so goldens stay byte-identical.
    /// </summary>
    public void Paint(UIR.ScreenBuffer buffer, UIR.Rect rect)
    {
        if (Text.Length == 0)
        {
            return;
        }

        int x1 = rect.X, x2 = rect.Right - 1;
        if (x2 - x1 <= Text.Length + 1)
        {
            return;
        }

        buffer.SetText(x1 + 2, rect.Y, Text, Style);
    }
}

/// <summary>
/// Typed drop-shadow part of <see cref="BorderPanel"/>. Owns its on/off state
/// and its own style; the root flips one property on this part instead of
/// carrying a shadow branch inside the frame painter (#2355).
/// </summary>
internal sealed class BorderShadowPart
{
    public BorderShadowPart(bool enabled = false) => Enabled = enabled;

    /// <summary>Whether the one-cell right + bottom shadow is painted.</summary>
    public bool Enabled { get; set; }

    /// <summary>The style this part resolved for its shadow cells.</summary>
    public UIR.CellStyle Style => BorderPanelStyles.Shadow;

    /// <summary>
    /// Paints the right + bottom strips outside <paramref name="rect"/>.
    /// <see cref="UIR.ScreenBuffer.Fill"/> clips to the buffer, so a
    /// flush-edge frame cannot index out of range.
    /// </summary>
    public void Paint(UIR.ScreenBuffer buffer, UIR.Rect rect)
    {
        if (!Enabled)
        {
            return;
        }

        int x1 = rect.X, y1 = rect.Y, x2 = rect.Right - 1, y2 = rect.Bottom - 1;
        var shade = UIR.Cell.From(new Rune(' '), Style);
        buffer.Fill(new UIR.Rect(x2 + 1, y1 + 1, 1, y2 - y1 + 1), shade);
        buffer.Fill(new UIR.Rect(x1 + 1, y2 + 1, x2 - x1 + 1, 1), shade);
    }
}
