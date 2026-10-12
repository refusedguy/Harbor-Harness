using System.Text;

namespace Harbor.Tui.CellForge.Generic.Widgets;

/// <summary>
/// Generic single-line text field over cells (Termina <c>TextInput</c> /
/// RazorConsole <c>TextInput</c> slot, epic #1195): rune-aware insert,
/// cursor motion and horizontal scroll with no chat coupling. The chat
/// composer stays the only chat input; this is the neutral primitive future
/// dialogs, palettes and forms compose from.
/// </summary>
public sealed class GenericTextField : IGenericFormField
{
    private string _text = string.Empty;
    private int _cursor;
    private int _scroll;

    /// <summary>Field label painted by <see cref="GenericForm"/>; empty draws no label.</summary>
    public string Label { get; set; } = string.Empty;

    /// <summary>Current text (UTF-16; rune-aware editing keeps surrogates paired).</summary>
    public string Text => _text;

    /// <summary>Cursor as a UTF-16 index into <see cref="Text"/>.</summary>
    public int Cursor => _cursor;

    /// <summary>Ghost hint painted dim when the field is empty.</summary>
    public string Placeholder { get; set; } = string.Empty;

    /// <summary>Mask rune (e.g. <c>'•'</c>); null paints the text verbatim.</summary>
    public Rune? Mask { get; set; }

    /// <summary>Maximum rune count; 0 means unlimited.</summary>
    public int MaxLength { get; set; }

    /// <summary>When true, empty text fails <see cref="Validate"/>.</summary>
    public bool Required { get; set; }

    /// <summary>Whether the cursor row renders (host focus state).</summary>
    public bool IsFocused { get; set; } = true;

    /// <inheritdoc />
    public int DesiredHeight => 1;

    /// <summary>Replaces the text and clamps the cursor.</summary>
    public void SetText(string text)
    {
        _text = text ?? string.Empty;
        _cursor = Math.Clamp(_cursor, 0, _text.Length);
        if (MaxLength > 0 && RuneCount(_text) > MaxLength)
        {
            _text = TakeRunes(_text, MaxLength);
        }

        _cursor = Math.Clamp(_cursor, 0, _text.Length);
    }

    /// <summary>Moves the cursor by a signed rune delta (clamped).</summary>
    public void MoveCursor(int delta)
    {
        if (delta == 0 || _text.Length == 0)
        {
            _cursor = delta < 0 ? 0 : _text.Length;
            return;
        }

        int cursor = _cursor;
        if (delta > 0)
        {
            for (int i = 0; i < delta && cursor < _text.Length; i++)
            {
                cursor += RuneLengthAt(_text, cursor);
            }
        }
        else
        {
            for (int i = 0; i < -delta && cursor > 0; i++)
            {
                cursor -= RuneLengthBefore(_text, cursor);
            }
        }

        _cursor = Math.Clamp(cursor, 0, _text.Length);
    }

    /// <summary>Inserts a printable rune at the cursor (obeys <see cref="MaxLength"/>).</summary>
    /// <returns>True when the text changed.</returns>
    public bool Insert(Rune rune)
    {
        if (Rune.IsControl(rune))
        {
            return false;
        }

        if (MaxLength > 0 && RuneCount(_text) >= MaxLength)
        {
            return false;
        }

        string s = rune.ToString();
        _text = _text.Insert(_cursor, s);
        _cursor += s.Length;
        return true;
    }

    /// <summary>Deletes the rune before the cursor (surrogate-pair aware).</summary>
    /// <returns>True when the text changed.</returns>
    public bool Backspace()
    {
        if (_cursor <= 0)
        {
            return false;
        }

        int len = RuneLengthBefore(_text, _cursor);
        _text = _text.Remove(_cursor - len, len);
        _cursor -= len;
        return true;
    }

    /// <summary>Deletes the rune at the cursor.</summary>
    /// <returns>True when the text changed.</returns>
    public bool DeleteAt()
    {
        if (_cursor >= _text.Length)
        {
            return false;
        }

        int len = RuneLengthAt(_text, _cursor);
        _text = _text.Remove(_cursor, len);
        return true;
    }

    /// <summary>Handles a key; Enter/Escape/Tab are left to the host focus chain.</summary>
    /// <returns>True when consumed.</returns>
    public bool HandleKey(in KeyEvent key)
    {
        if (key.EventType == KeyEventType.Release)
        {
            return false;
        }

        switch (key.Key)
        {
            case KeyCode.Char when key.Modifiers.AcceptsTypedChar():
                return Insert(key.Character);
            case KeyCode.Char:
                return false;
            case KeyCode.Backspace:
                return Backspace();
            case KeyCode.Delete:
                return DeleteAt();
            case KeyCode.Left:
                MoveCursor(-1);
                return true;
            case KeyCode.Right:
                MoveCursor(1);
                return true;
            case KeyCode.Home:
                _cursor = 0;
                return true;
            case KeyCode.End:
                _cursor = _text.Length;
                return true;
            default:
                return false;
        }
    }

    /// <inheritdoc />
    public string? Validate() =>
        Required && _text.Length == 0 ? "Required." : null;

    /// <summary>Paints the visible slice into the first row of <paramref name="rect"/>.</summary>
    public void Paint(ScreenBuffer buffer, Rect rect)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        if (rect.Width <= 0 || rect.Height <= 0)
        {
            return;
        }

        string source = _text;
        string visible = Mask is { } mask
            ? new string((char)mask.Value, RuneCount(source))
            : source;
        CellStyle textStyle = new(ChatPalette.Text);
        CellStyle hintStyle = ChatPalette.Dim;

        if (visible.Length == 0)
        {
            if (Placeholder.Length > 0)
            {
                buffer.SetText(rect.X, rect.Y, Placeholder.AsSpan(0, Math.Min(Placeholder.Length, rect.Width)), hintStyle);
            }

            if (IsFocused)
            {
                PaintCursor(buffer, rect.X, rect.Y, hintStyle);
            }

            return;
        }

        int cursorCells = Mask is null
            ? CellsBefore(visible, Math.Clamp(_cursor, 0, visible.Length))
            : RuneIndexAt(source, _cursor);
        int width = rect.Width;
        if (cursorCells < _scroll)
        {
            _scroll = cursorCells;
        }
        else if (cursorCells >= _scroll + width)
        {
            _scroll = cursorCells - width + 1;
        }

        int startChar = CharIndexForCells(visible, _scroll);
        int endChar = CharIndexForCells(visible, _scroll + width);
        buffer.SetText(rect.X, rect.Y, visible.AsSpan(startChar, endChar - startChar), textStyle);

        if (IsFocused)
        {
            int cursorX = rect.X + (cursorCells - _scroll);
            if (cursorX >= rect.X && cursorX < rect.X + width)
            {
                PaintCursor(buffer, cursorX, rect.Y, textStyle);
            }
        }
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

    private static int RuneCount(string s)
    {
        int n = 0;
        var span = s.AsSpan();
        while (!span.IsEmpty)
        {
            Rune.DecodeFromUtf16(span, out _, out int consumed);
            if (consumed <= 0)
            {
                break;
            }

            n++;
            span = span[consumed..];
        }

        return n;
    }

    private static string TakeRunes(string s, int count)
    {
        var span = s.AsSpan();
        int taken = 0;
        int n = 0;
        while (!span.IsEmpty && n < count)
        {
            Rune.DecodeFromUtf16(span, out _, out int consumed);
            if (consumed <= 0)
            {
                break;
            }

            taken += consumed;
            n++;
            span = span[consumed..];
        }

        return s[..taken];
    }

    private static int RuneLengthAt(string s, int index)
    {
        if ((uint)index >= (uint)s.Length)
        {
            return 0;
        }

        Rune.DecodeFromUtf16(s.AsSpan(index), out _, out int consumed);
        return consumed > 0 ? consumed : 1;
    }

    private static int RuneLengthBefore(string s, int index)
    {
        if (index <= 0)
        {
            return 0;
        }

        if (index >= 2 && char.IsLowSurrogate(s[index - 1]) && char.IsHighSurrogate(s[index - 2]))
        {
            return 2;
        }

        return 1;
    }

    private static int CellsBefore(string s, int charIndex)
    {
        int cells = 0;
        var span = s.AsSpan(0, Math.Min(charIndex, s.Length));
        while (!span.IsEmpty)
        {
            Rune.DecodeFromUtf16(span, out Rune rune, out int consumed);
            if (consumed <= 0)
            {
                break;
            }

            cells += Math.Max(1, UnicodeWidth.Width(rune));
            span = span[consumed..];
        }

        return cells;
    }

    private static int RuneIndexAt(string s, int charIndex)
    {
        int runes = 0;
        int i = 0;
        int target = Math.Clamp(charIndex, 0, s.Length);
        while (i < target)
        {
            Rune.DecodeFromUtf16(s.AsSpan(i), out _, out int consumed);
            if (consumed <= 0)
            {
                break;
            }

            i += consumed;
            runes++;
        }

        return runes;
    }

    private static int CharIndexForCells(string s, int targetCells)
    {
        int cells = 0;
        int i = 0;
        while (i < s.Length)
        {
            Rune.DecodeFromUtf16(s.AsSpan(i), out Rune rune, out int consumed);
            if (consumed <= 0)
            {
                break;
            }

            int w = Math.Max(1, UnicodeWidth.Width(rune));
            if (cells + w > targetCells)
            {
                break;
            }

            cells += w;
            i += consumed;
        }

        return i;
    }
}
