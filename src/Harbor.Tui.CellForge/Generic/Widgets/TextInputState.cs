using System.Text;

namespace Harbor.Tui.CellForge.Generic.Widgets;

/// <summary>
/// External state for the text-input primitive (#2362). The widget paints and
/// mutates this object but never owns it: a host keeps the state across frames,
/// swaps it between views, or hands the same instance to two widgets. Editing
/// is rune-aware, so a surrogate pair is inserted, deleted and skipped as one
/// character.
/// </summary>
public sealed class TextInputState
{
    private string _text = string.Empty;
    private int _cursor;

    /// <summary>Current text as UTF-16. Mutations go through the edit methods.</summary>
    public string Text => _text;

    /// <summary>Caret as a UTF-16 index into <see cref="Text"/>.</summary>
    public int Cursor => _cursor;

    /// <summary>Horizontal scroll in display cells; the widget clamps it during paint.</summary>
    public int ScrollX { get; set; }

    /// <summary>Display mask rune (for example a password dot). Null paints text verbatim.</summary>
    public Rune? Mask { get; set; }

    /// <summary>Maximum rune count; 0 means unlimited.</summary>
    public int MaxLength { get; set; }

    /// <summary>When true, empty text fails <see cref="Validate"/>.</summary>
    public bool Required { get; set; }

    /// <summary>When true, newline runes are accepted and kept in the text.</summary>
    public bool Multiline { get; set; }

    /// <summary>
    /// Validation hook. Returns an error message, or null when the value is
    /// acceptable. Runs before the <see cref="Required"/> check so a custom
    /// message wins over the generic one.
    /// </summary>
    public Func<string, string?>? Validator { get; set; }

    /// <summary>Replaces the text and clamps the caret into the new value.</summary>
    public void SetText(string text)
    {
        string value = text ?? string.Empty;
        if (!Multiline)
        {
            value = value.Replace('\n', ' ');
        }

        if (MaxLength > 0 && TextRunes.Count(value) > MaxLength)
        {
            value = TextRunes.Take(value, MaxLength);
        }

        _text = value;
        _cursor = Math.Clamp(_cursor, 0, _text.Length);
    }

    /// <summary>Inserts one rune at the caret. Returns true when the text changed.</summary>
    public bool Insert(Rune rune)
    {
        bool newline = rune.Value == '\n';
        if (newline && !Multiline)
        {
            return false;
        }

        if (!newline && Rune.IsControl(rune))
        {
            return false;
        }

        if (MaxLength > 0 && TextRunes.Count(_text) >= MaxLength)
        {
            return false;
        }

        string s = rune.ToString();
        _text = _text.Insert(_cursor, s);
        _cursor += s.Length;
        return true;
    }

    /// <summary>Inserts a string at the caret. Returns true when the text changed.</summary>
    public bool InsertText(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        if (!Multiline && text.Contains('\n'))
        {
            text = text.Replace('\n', ' ');
        }

        if (MaxLength > 0)
        {
            int room = MaxLength - TextRunes.Count(_text);
            if (room <= 0)
            {
                return false;
            }

            text = TextRunes.Take(text, room);
            if (text.Length == 0)
            {
                return false;
            }
        }

        _text = _text.Insert(_cursor, text);
        _cursor += text.Length;
        return true;
    }

    /// <summary>Deletes the rune before the caret. Returns true when the text changed.</summary>
    public bool Backspace()
    {
        if (_cursor <= 0)
        {
            return false;
        }

        int len = TextRunes.LengthBefore(_text, _cursor);
        _text = _text.Remove(_cursor - len, len);
        _cursor -= len;
        return true;
    }

    /// <summary>Deletes the rune at the caret. Returns true when the text changed.</summary>
    public bool DeleteForward()
    {
        if (_cursor >= _text.Length)
        {
            return false;
        }

        int len = TextRunes.LengthAt(_text, _cursor);
        _text = _text.Remove(_cursor, len);
        return true;
    }

    /// <summary>Moves the caret by a signed rune delta and returns true when it moved.</summary>
    public bool MoveCursor(int delta)
    {
        if (delta == 0)
        {
            return false;
        }

        int cursor = _cursor;
        if (delta > 0)
        {
            for (int i = 0; i < delta && cursor < _text.Length; i++)
            {
                cursor += TextRunes.LengthAt(_text, cursor);
            }
        }
        else
        {
            for (int i = 0; i < -delta && cursor > 0; i++)
            {
                cursor -= TextRunes.LengthBefore(_text, cursor);
            }
        }

        if (cursor == _cursor)
        {
            return false;
        }

        _cursor = cursor;
        return true;
    }

    /// <summary>Moves the caret to an absolute UTF-16 offset, clamped to the text.</summary>
    public bool MoveTo(int offset)
    {
        int target = Math.Clamp(offset, 0, _text.Length);
        if (target == _cursor)
        {
            return false;
        }

        _cursor = target;
        return true;
    }

    /// <summary>Runs the validation hook, then the required check.</summary>
    public string? Validate()
    {
        if (Validator is { } validate && validate(_text) is { } error)
        {
            return error;
        }

        return Required && _text.Length == 0 ? "Required." : null;
    }
}
