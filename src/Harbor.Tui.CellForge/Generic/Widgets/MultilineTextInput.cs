using System.Buffers;
using System.Text;
using Ensure = Harbor.Abstractions.Extensions.Ensure;

namespace Harbor.Tui.CellForge.Generic.Widgets;

/// <summary>
/// Multiline text-input widget (#2362). It wraps the host-owned
/// <see cref="TextInputState"/> at the field width using word boundaries and
/// grows its row count with the wrapped content. Explicit newlines stay hard
/// breaks; a word wider than the field breaks at a rune boundary. Editing and
/// validation stay in the state, so this class only decides where glyphs land.
/// </summary>
public sealed class MultilineTextInput : ITextInput
{
    private readonly TextInputChrome _chrome = new();
    private readonly List<Line> _lines = new();

    private readonly record struct Line(int Start, int Length);

    public MultilineTextInput(TextInputState state)
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

    /// <summary>Field width used for wrapping and autogrow; paint updates it from the rect.</summary>
    public int WrapWidth { get; set; } = 40;

    /// <summary>Rows to grow to, or 0 for no cap.</summary>
    public int MaxRows { get; set; }

    /// <inheritdoc />
    public int DesiredHeight
    {
        get
        {
            int rows = Math.Max(1, Layout(State.Text, Math.Max(1, WrapWidth)).Count);
            return MaxRows > 0 ? Math.Min(rows, MaxRows) : rows;
        }
    }

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
        int fieldW = rect.Width - _chrome.PrefixCells - _chrome.SuffixCells;
        if (fieldW <= 0)
        {
            return;
        }

        WrapWidth = fieldW;
        List<Line> lines = Layout(State.Text, fieldW);
        var textStyle = new CellStyle(ChatPalette.Text);

        for (int r = 0; r < lines.Count && r < rect.Height; r++)
        {
            int y = rect.Y + r;
            _chrome.PaintPrefix(buffer, new Rect(rect.X, y, rect.Width, 1));

            Line line = lines[r];
            if (State.Mask is { } mask)
            {
                int runes = TextRunes.Count(State.Text.Substring(line.Start, line.Length));
                buffer.SetText(rect.X + _chrome.PrefixCells, y, new string((char)mask.Value, runes).AsSpan(), textStyle);
            }
            else
            {
                buffer.SetText(rect.X + _chrome.PrefixCells, y, State.Text.AsSpan(line.Start, line.Length), textStyle);
            }

            _chrome.PaintSuffix(buffer, new Rect(rect.X, y, rect.Width, 1));
        }

        if (!IsFocused)
        {
            return;
        }

        (int row, int col) = CursorPosition(lines, State.Cursor);
        if (row >= rect.Height)
        {
            return;
        }

        int cursorX = rect.X + _chrome.PrefixCells + col;
        if ((uint)cursorX < (uint)buffer.Cols)
        {
            PaintCursor(buffer, cursorX, rect.Y + row, textStyle);
        }
    }

    /// <summary>Breaks the text into wrapped visual lines.</summary>
    private List<Line> Layout(string text, int width)
    {
        _lines.Clear();
        width = Math.Max(1, width);
        int i = 0;
        while (i <= text.Length)
        {
            int newline = text.IndexOf('\n', i);
            int end = newline < 0 ? text.Length : newline;
            WrapLogical(text, i, end, width);
            if (newline < 0)
            {
                break;
            }

            i = newline + 1;
        }

        return _lines;
    }

    private void WrapLogical(string text, int start, int end, int width)
    {
        if (start == end)
        {
            _lines.Add(new Line(start, 0));
            return;
        }

        int lineStart = start;
        int cells = 0;
        int lastSpace = -1;
        int i = start;
        while (i < end)
        {
            if (Rune.DecodeFromUtf16(text.AsSpan(i, end - i), out var rune, out int consumed) != OperationStatus.Done)
            {
                consumed = 1;
                rune = new Rune('\uFFFD');
            }

            int w = Math.Max(1, UnicodeWidth.Width(rune));
            if (cells + w > width && i > lineStart)
            {
                int breakAt = lastSpace > lineStart ? lastSpace : i;
                _lines.Add(new Line(lineStart, breakAt - lineStart));
                lineStart = breakAt;
                while (lineStart < end && char.IsWhiteSpace(text[lineStart]))
                {
                    lineStart++;
                }

                i = lineStart;
                cells = 0;
                lastSpace = -1;
                continue;
            }

            if (rune.Value is ' ' or '\t')
            {
                lastSpace = i;
            }

            cells += w;
            i += consumed;
        }

        _lines.Add(new Line(lineStart, end - lineStart));
    }

    private (int Row, int Col) CursorPosition(List<Line> lines, int cursor)
    {
        for (int i = 0; i < lines.Count; i++)
        {
            Line line = lines[i];
            if (cursor >= line.Start && cursor <= line.Start + line.Length)
            {
                return (i, TextRunes.CellsBefore(State.Text.AsSpan(line.Start, Math.Min(cursor - line.Start, line.Length))));
            }
        }

        return (0, 0);
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
