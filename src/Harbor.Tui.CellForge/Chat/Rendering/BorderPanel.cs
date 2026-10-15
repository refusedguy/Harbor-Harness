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
/// Box-drawing frame panel — the minimal concrete painter used by golden
/// grid-dump tests. Focus switches the border to bold accent style.
/// </summary>
public class BorderPanel : Panel
{
    private static readonly UIR.CellStyle FrameStyle = new(UIR.PackedColor.Indexed(8));
    private static readonly UIR.CellStyle FocusedStyle = new(attrs: UIR.StyleAttr.Bold);
    private static readonly UIR.CellStyle ShadowStyle = new(bg: UIR.PackedColor.Indexed(8));

    public BorderPanel(string id, int minWidth, int minHeight, int priority = 0, string title = "")
        : base(id, new Size(minWidth, minHeight), priority)
    {
        Title = title;
    }

    public string Title { get; set; }

    /// <summary>Box glyph set; <see cref="BorderKind.Plain"/> is the default.</summary>
    public BorderKind BorderKind { get; set; } = BorderKind.Plain;

    /// <summary>One-cell drop shadow (right + bottom), clipped to the buffer.</summary>
    public bool Shadow { get; set; }

    /// <summary>
    /// Content area inside the 1-cell frame (ratatui <c>Block::inner</c>): the
    /// title sits on the top border and the shadow paints outside
    /// <see cref="Panel.Rect"/>, so neither takes an extra inset.
    /// </summary>
    public override UIR.Rect ContentRect => InsetContent(Rect, 1, 1, 1, 1);

    public override void Paint(UIR.ScreenBuffer buffer)
    {
        var r = Rect;
        if (r.Width < 2 || r.Height < 2)
        {
            return;
        }

        var style = Focused ? FocusedStyle : FrameStyle;
        char topLeftCh = '┌', topRightCh = '┐', bottomLeftCh = '└', bottomRightCh = '┘';
        char horizCh = '─', vertCh = '│';
        switch (BorderKind)
        {
            case BorderKind.Rounded:
                topLeftCh = '╭';
                topRightCh = '╮';
                bottomLeftCh = '╰';
                bottomRightCh = '╯';
                break;
            case BorderKind.Double:
                topLeftCh = '╔';
                topRightCh = '╗';
                bottomLeftCh = '╚';
                bottomRightCh = '╝';
                horizCh = '═';
                vertCh = '║';
                break;
            case BorderKind.Thick:
                topLeftCh = '┏';
                topRightCh = '┓';
                bottomLeftCh = '┗';
                bottomRightCh = '┛';
                horizCh = '━';
                vertCh = '┃';
                break;
        }

        var topLeft = UIR.Cell.From(new Rune(topLeftCh), style);
        var topRight = UIR.Cell.From(new Rune(topRightCh), style);
        var bottomLeft = UIR.Cell.From(new Rune(bottomLeftCh), style);
        var bottomRight = UIR.Cell.From(new Rune(bottomRightCh), style);
        var horiz = UIR.Cell.From(new Rune(horizCh), style);
        var vert = UIR.Cell.From(new Rune(vertCh), style);

        int x1 = r.X, y1 = r.Y, x2 = r.Right - 1, y2 = r.Bottom - 1;

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

        if (Shadow)
        {
            // One-cell drop shadow (right + bottom). Fill clips to the
            // buffer, so a flush-edge frame cannot index out of range.
            var shade = UIR.Cell.From(new Rune(' '), ShadowStyle);
            buffer.Fill(new UIR.Rect(x2 + 1, y1 + 1, 1, y2 - y1 + 1), shade);
            buffer.Fill(new UIR.Rect(x1 + 1, y2 + 1, x2 - x1 + 1, 1), shade);
        }

        if (Title.Length > 0 && x2 - x1 > Title.Length + 1)
        {
            buffer.SetText(x1 + 2, y1, Title, style);
        }
    }
}
