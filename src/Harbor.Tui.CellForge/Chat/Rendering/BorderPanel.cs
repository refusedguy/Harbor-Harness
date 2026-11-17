using System.Text;
using UIR = Harbor.Ui.Framework.Rendering;

namespace Harbor.Tui.CellForge.Rendering;

/// <summary>
/// Box glyph set for <see cref="BorderPanel"/> (ratatui <c>BorderType</c>).
/// </summary>
public enum BorderKind : byte
{
    /// <summary>Single lines: ┌┐└┘ ─ │ (default, golden-compatible).</summary>
    Plain = 0,

    /// <summary>Curved corners: ╭╮╰╯ ─ │.</summary>
    Rounded = 1,

    /// <summary>Double lines: ╔╗╚╝ ═ ║.</summary>
    Double = 2,

    /// <summary>Heavy lines: ┏┓┗┛ ━ ┃.</summary>
    Thick = 3,
}

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

/// <summary>
/// Box-drawing frame panel — the minimal concrete painter used by golden
/// grid-dump tests. Focus switches the border to bold accent style.
///
/// Composite of three explicit typed parts (#2355, gpui-kit
/// <c>Checkbox / CheckboxIndicator</c> discipline): <see cref="BorderFramePart"/>
/// owns the glyph set + frame style, <see cref="BorderTitlePart"/> owns the
/// title + its style, <see cref="BorderShadowPart"/> owns the shadow + its
/// style. Each part resolves its own style from its own state. This root owns
/// the single <see cref="Panel.Rect"/> (the one geometry owner) and the focus
/// flag; it neither walks a child tree by name nor threads a style flag into
/// a part — it syncs the one shared state (focus) and delegates painting.
/// Parts stay in-tree host-side panel state, not an <c>INode</c> or a screen
/// projector, and no extension axis is introduced.
///
/// The parts live in this file on purpose: <c>PanelChromeTests.BoxCorners_AreSpelledInExactlyOneFile</c>
/// permits corner glyphs only here and in <c>PanelChrome.cs</c>. Splitting the
/// painter into another source file would spell a third box painter even
/// though the parts are the same one.
/// </summary>
public class BorderPanel : Panel
{
    private readonly BorderFramePart _frame = new();
    private readonly BorderTitlePart _title = new();
    private readonly BorderShadowPart _shadow = new();

    public BorderPanel(string id, int minWidth, int minHeight, int priority = 0, string title = "")
        : base(id, new Size(minWidth, minHeight), priority)
    {
        _title.Text = title;
    }

    /// <summary>
    /// Title text. The state lives on <see cref="BorderTitlePart"/>, so the
    /// part that paints the title is also the part that owns the text.
    /// </summary>
    public string Title
    {
        get => _title.Text;
        set => _title.Text = value;
    }

    /// <summary>Box glyph set; <see cref="BorderKind.Plain"/> is the default.</summary>
    public BorderKind BorderKind
    {
        get => _frame.Kind;
        set => _frame.Kind = value;
    }

    /// <summary>
    /// One-cell drop shadow (right + bottom), clipped to the buffer. Owned by
    /// <see cref="BorderShadowPart"/>.
    /// </summary>
    public bool Shadow
    {
        get => _shadow.Enabled;
        set => _shadow.Enabled = value;
    }

    /// <summary>
    /// Content area inside the 1-cell frame (ratatui <c>Block::inner</c>): the
    /// title sits on the top border and the shadow paints outside
    /// <see cref="Panel.Rect"/>, so neither takes an extra inset. Derived from
    /// the root's single rect — geometry has one owner.
    /// </summary>
    public override UIR.Rect ContentRect => InsetContent(Rect, 1, 1, 1, 1);

    public override void Paint(UIR.ScreenBuffer buffer)
    {
        var r = Rect;
        if (r.Width < 2 || r.Height < 2)
        {
            return;
        }

        // The root owns focus (set by the layout solver); sync it into the two
        // parts that style from it, then delegate. No descendant search, no
        // style parameter passed down — every part resolves its own style.
        _frame.Focused = Focused;
        _title.Focused = Focused;

        _frame.Paint(buffer, r);
        _shadow.Paint(buffer, r);
        _title.Paint(buffer, r);
    }
}
