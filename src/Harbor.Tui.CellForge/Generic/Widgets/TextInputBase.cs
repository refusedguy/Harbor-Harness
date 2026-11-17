using System.Text;
using Ensure = Harbor.Abstractions.Extensions.Ensure;

namespace Harbor.Tui.CellForge.Generic.Widgets;

/// <summary>
/// Shared contract plumbing for the text-input widgets (#2362). The state is
/// supplied by the host and never created here; subclasses only decide how to
/// map it onto rows and cells.
/// </summary>
public abstract class TextInputBase : ITextInput
{
    protected TextInputBase(TextInputState state)
    {
        Ensure.NotNull(state);
        State = state;
    }

    /// <summary>Affix layout and button hit rectangles, shared by both widgets.</summary>
    protected TextInputChrome Chrome { get; } = new();

    /// <inheritdoc />
    public TextInputState State { get; }

    /// <inheritdoc />
    public TextInputAffixes Affixes
    {
        get => Chrome.Affixes;
        set => Chrome.Affixes = value ?? TextInputAffixes.None;
    }

    /// <inheritdoc />
    public bool IsFocused { get; set; } = true;

    /// <inheritdoc />
    public abstract int DesiredHeight { get; }

    /// <inheritdoc />
    public string? Validate() => State.Validate();

    /// <inheritdoc />
    public string? HitTest(int x, int y) => Chrome.HitTest(x, y);

    /// <inheritdoc />
    public abstract void Paint(ScreenBuffer buffer, Rect rect);

    /// <summary>Paints the caret by reversing the cell it covers.</summary>
    protected static void PaintCursor(ScreenBuffer buffer, int x, int y, CellStyle baseStyle)
    {
        if ((uint)x >= (uint)buffer.Cols || (uint)y >= (uint)buffer.Rows)
        {
            return;
        }

        var style = new CellStyle(baseStyle.Fg, baseStyle.Bg, baseStyle.Attrs | StyleAttr.Reverse);
        int rune = buffer.Get(x, y).Rune;
        Rune r = rune == 0 || rune == ' ' ? new Rune('_') : new Rune(rune);
        buffer.SetRune(x, y, r, in style);
    }
}
