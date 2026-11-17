using Ensure = Harbor.Abstractions.Extensions.Ensure;

namespace Harbor.Tui.CellForge.Generic.Widgets;

/// <summary>
/// Single-line text-input widget (#2362). It renders the host-owned
/// <see cref="TextInputState"/> with a display mask, a caret and affix slots,
/// and delegates all editing to the state. No chat coupling and no key routing:
/// the host calls the state's edit methods from its own input loop.
/// </summary>
public sealed class TextInput : TextInputBase
{
    public TextInput(TextInputState state)
        : base(state)
    {
    }

    /// <inheritdoc />
    public override int DesiredHeight => 1;

    /// <inheritdoc />
    public override void Paint(ScreenBuffer buffer, Rect rect)
    {
        Ensure.NotNull(buffer);
        if (rect.Width <= 0 || rect.Height <= 0)
        {
            return;
        }

        Chrome.Reset();
        Chrome.PaintPrefix(buffer, rect);

        int fieldX = rect.X + Chrome.PrefixCells;
        int fieldW = rect.Width - Chrome.PrefixCells - Chrome.SuffixCells;
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
                TextRunes.PaintCursor(buffer, fieldX, rect.Y, hintStyle);
            }

            Chrome.PaintSuffix(buffer, rect);
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
                TextRunes.PaintCursor(buffer, cursorX, rect.Y, textStyle);
            }
        }

        Chrome.PaintSuffix(buffer, rect);
    }
}
