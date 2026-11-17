using Ensure = Harbor.Abstractions.Extensions.Ensure;

namespace Harbor.Tui.CellForge.Generic.Widgets;

/// <summary>
/// Single-line text-input widget (#2362). It renders the host-owned
/// <see cref="TextInputState"/> with a display mask, a caret and affix slots,
/// and delegates all editing to the state. No chat coupling and no key routing:
/// the host calls the state's edit methods from its own input loop.
/// </summary>
public sealed class TextInput : ITextInput
{
    private readonly TextInputChrome _chrome = new();

    public TextInput(TextInputState state)
    {
        Ensure.NotNull(state);
        State = state;
    }

    /// <inheritdoc />
    public TextInputState State { get; }

    /// <inheritdoc />
    public TextInputAffixes Affixes
    {
        get => _chrome.Affixes;
        set => _chrome.Affixes = value ?? TextInputAffixes.None;
    }

    /// <inheritdoc />
    public bool IsFocused { get; set; } = true;

    /// <inheritdoc />
    public int DesiredHeight => 1;

    /// <inheritdoc />
    public string? Validate() => State.Validate();

    /// <inheritdoc />
    public string? HitTest(int x, int y) => _chrome.HitTest(x, y);

    /// <inheritdoc />
    public void Paint(ScreenBuffer buffer, Rect rect)
    {
        Ensure.NotNull(buffer);
        if (rect.Width <= 0 || rect.Height <= 0)
        {
            return;
        }

        _chrome.Reset();
        _chrome.PaintPrefix(buffer, rect);

        int fieldX = rect.X + _chrome.PrefixCells;
        int fieldW = rect.Width - _chrome.PrefixCells - _chrome.SuffixCells;
        if (fieldW <= 0)
        {
            return;
        }

        string source = State.Text;
        string visible = State.Mask is { } mask
            ? new string((char)mask.Value, TextRunes.Count(source))
            : source;
        var textStyle = new CellStyle(ChatPalette.Text);
        var hintStyle = ChatPalette.Dim;

        if (visible.Length == 0)
        {
            if (IsFocused)
            {
                PaintCursor(buffer, fieldX, rect.Y, hintStyle);
            }

            _chrome.PaintSuffix(buffer, rect);
            return;
        }

        int cursorCells = State.Mask is null
            ? TextRunes.CellsBefore(source.AsSpan(0, Math.Clamp(State.Cursor, 0, source.Length)))
            : TextRunes.RuneIndexAt(source, State.Cursor);

        int scroll = State.ScrollX;
        if (cursorCells < scroll)
        {
            scroll = cursorCells;
        }
        else if (cursorCells >= scroll + fieldW)
        {
            scroll = cursorCells - fieldW + 1;
        }

        State.ScrollX = scroll;
        int startChar = TextRunes.CharIndexForCells(visible, scroll);
        int endChar = TextRunes.CharIndexForCells(visible, scroll + fieldW);
        buffer.SetText(fieldX, rect.Y, visible.AsSpan(startChar, endChar - startChar), textStyle);

        if (IsFocused)
        {
            int cursorX = fieldX + cursorCells - scroll;
            if (cursorX >= fieldX && cursorX < fieldX + fieldW)
            {
                PaintCursor(buffer, cursorX, rect.Y, textStyle);
            }
        }

        _chrome.PaintSuffix(buffer, rect);
    }

    private static void PaintCursor(ScreenBuffer buffer, int x, int y, CellStyle baseStyle)
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
