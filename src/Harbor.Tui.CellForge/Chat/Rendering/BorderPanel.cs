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
