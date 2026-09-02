using System.Buffers;
using System.Text;

namespace Harbor.Tui.CellForge.Rendering;

/// <summary>Discriminator for the minimal-redraw outcome of a buffer edit.</summary>
public enum EditOutcomeKind : byte
{
    /// <summary>Nothing changed (no-op key) — nothing to redraw.</summary>
    Unchanged = 0,

    /// <summary>Only the caret moved — repaint the cursor cell pair.</summary>
    CursorOnly = 1,

    /// <summary>Text changed, caret stayed — repaint the affected range.</summary>
    TextOnly = 2,

    /// <summary>Text and caret changed — repaint the affected range plus caret.</summary>
    TextAndCursor = 3,
}

/// <summary>
/// Redraw hint produced by every <see cref="PromptBuffer"/> operation
/// (grok EditOutcome pattern): the view decides between full-line repaint and
/// cursor-only patching without comparing snapshots.
/// </summary>
public readonly record struct EditOutcome(EditOutcomeKind Kind, int TextStart, int TextEnd)
{
    public static readonly EditOutcome Unchanged = default;

    public static EditOutcome Cursor() => new(EditOutcomeKind.CursorOnly, 0, 0);

    public static EditOutcome Text(int start, int end, bool movedCursor) =>
        new(movedCursor ? EditOutcomeKind.TextAndCursor : EditOutcomeKind.TextOnly, start, end);
}

/// <summary>
/// Single-line-first prompt editor model: one growable char array + UTF-16
/// caret that never sits inside a surrogate pair. Shift+Enter inserts a
/// literal newline so the buffer can hold multiple logical lines; horizontal
/// overflow is handled by <see cref="PromptViewport.ScrollIntoView"/>, not by
/// soft wrapping (WrapCache joins in CE-2 when the grid lands).
///
/// Allocation budget: movement/cursor ops are zero-alloc; text edits grow the
/// backing array geometrically; <see cref="SnapshotText"/> allocates on demand.
/// </summary>
public sealed class PromptBuffer
{
    private const int InitialCapacity = 256;

    private char[] _buf = new char[InitialCapacity];
    private int _length;
    private int _cursor;

    public int Length => _length;
    public int Cursor => _cursor;
    public bool IsEmpty => _length == 0;

    /// <summary>Number of logical lines (1 + count of embedded newlines).</summary>
    public int LineCount
    {
        get
        {
            int lines = 1;
            for (int i = 0; i < _length; i++)
            {
                if (_buf[i] == '\n')
                {
                    lines++;
                }
            }

            return lines;
        }
    }

    /// <summary>Copies current content into a string (submit path).</summary>
    public string SnapshotText() => new(_buf, 0, _length);

    /// <summary>
    /// Live view over current content — zero-alloc read for the every-frame
    /// composer paint. Valid until the next edit (edits may grow/reorder the
    /// backing array); painters consume the span within the frame.
    /// </summary>
    public ReadOnlySpan<char> AsSpan() => _buf.AsSpan(0, _length);

    /// <summary>
    /// Text of the last completed readline kill (Ctrl+U/W/K, Alt+D).
    /// Backspace/DeleteForward are not kills; a no-op kill never clobbers the
    /// previous entry. Single-slot kill ring backing the composer's Ctrl+Y yank.
    /// </summary>
    public string? LastKill { get; private set; }

    /// <summary>Takes the content and resets the buffer (Enter-submit).</summary>
    public string TakeText()
    {
        var text = SnapshotText();
        _length = 0;
        _cursor = 0;
        PurgeHistory();
        return text;
    }

    public void Clear()
    {
        _length = 0;
        _cursor = 0;
        PurgeHistory();
    }

    // ── Edits ──────────────────────────────────────────────────────────────

    public EditOutcome Insert(Rune rune)
    {
        Checkpoint();
        int size = rune.Utf16SequenceLength;
        EnsureCapacity(_length + size);
        Array.Copy(_buf, _cursor, _buf, _cursor + size, _length - _cursor);
        if (size == 1)
        {
            _buf[_cursor++] = (char)rune.Value;
        }
        else
        {
            Span<char> tmp = stackalloc char[2];
            _ = rune.EncodeToUtf16(tmp);
            tmp.CopyTo(_buf.AsSpan(_cursor));
            _cursor += 2;
        }

        _length += size;
        return EditOutcome.Text(_cursor - size, _cursor, movedCursor: true);
    }

    public EditOutcome InsertText(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return EditOutcome.Unchanged;
        }

        Checkpoint();
        EnsureCapacity(_length + text.Length);
        Array.Copy(_buf, _cursor, _buf, _cursor + text.Length, _length - _cursor);
        text.CopyTo(0, _buf, _cursor, text.Length);
        _cursor += text.Length;
        _length += text.Length;
        return EditOutcome.Text(_cursor - text.Length, _cursor, movedCursor: true);
    }

    public EditOutcome Backspace()
    {
        if (_cursor == 0)
        {
            return EditOutcome.Unchanged;
        }

        Checkpoint();
        int start = PrevRuneBoundary(_cursor);
        int removed = _cursor - start;
        Array.Copy(_buf, _cursor, _buf, start, _length - _cursor);
        _length -= removed;
        _cursor = start;
        return EditOutcome.Text(start, start, movedCursor: true);
    }

    public EditOutcome DeleteForward()
    {
        if (_cursor >= _length)
        {
            return EditOutcome.Unchanged;
        }

        Checkpoint();
        int end = NextRuneBoundary(_cursor);
        int removed = end - _cursor;
        Array.Copy(_buf, end, _buf, _cursor, _length - end);
        _length -= removed;
        return EditOutcome.Text(_cursor, _cursor, movedCursor: false);
    }

    /// <summary>Ctrl+U: remove everything before the caret on the current line.</summary>
    public EditOutcome DeleteToLineStart()
    {
        int lineStart = LineStartOf(_cursor);
        if (_cursor == lineStart)
        {
            return EditOutcome.Unchanged;
        }

        Checkpoint();
        int removed = _cursor - lineStart;
        LastKill = new string(_buf, lineStart, removed);
        Array.Copy(_buf, _cursor, _buf, lineStart, _length - _cursor);
        _length -= removed;
        _cursor = lineStart;
        return EditOutcome.Text(lineStart, lineStart, movedCursor: true);
    }

    /// <summary>Ctrl+W: remove the word before the caret (whitespace-delimited).</summary>
    public EditOutcome DeleteWordBackward()
    {
        if (_cursor == 0)
        {
            return EditOutcome.Unchanged;
        }

        int i = _cursor;
        while (i > 0 && char.IsWhiteSpace(_buf[i - 1]))
        {
            i--;
        }

        while (i > 0 && !char.IsWhiteSpace(_buf[i - 1]))
        {
            i--;
        }

        if (i == _cursor)
        {
            return EditOutcome.Unchanged;
        }

        Checkpoint();
        int removed = _cursor - i;
        LastKill = new string(_buf, i, removed);
        Array.Copy(_buf, _cursor, _buf, i, _length - _cursor);
        _length -= removed;
        _cursor = i;
        return EditOutcome.Text(i, i, movedCursor: true);
    }

    /// <summary>Ctrl+K: remove everything from the caret to the end of the current line.</summary>
    public EditOutcome DeleteToLineEnd()
    {
        int lineEnd = LineEndOf(_cursor);
        if (_cursor == lineEnd)
        {
            return EditOutcome.Unchanged;
        }

        Checkpoint();
        int removed = lineEnd - _cursor;
        LastKill = new string(_buf, _cursor, removed);
        Array.Copy(_buf, lineEnd, _buf, _cursor, _length - lineEnd);
        _length -= removed;
        return EditOutcome.Text(_cursor, _cursor, movedCursor: false);
    }

    /// <summary>
    ///     Kill-ring-free readline <c>M-d</c>: remove the word in front of the
    ///     caret up to the next whitespace run. At end-of-line this mirrors
    ///     <see cref="MoveWordRight" /> boundaries so <c>Alt+d</c>/<c>Alt+b</c>
    ///     agree on what "word" means.
    /// </summary>
    public EditOutcome DeleteWordForward()
    {
        if (_cursor >= _length)
        {
            return EditOutcome.Unchanged;
        }

        Checkpoint();
        int i = _cursor;
        while (i < _length && char.IsWhiteSpace(_buf[i]))
        {
            i++;
        }

        while (i < _length && !char.IsWhiteSpace(_buf[i]))
        {
            i++;
        }

        int removed = i - _cursor;
        LastKill = new string(_buf, _cursor, removed);
        Array.Copy(_buf, i, _buf, _cursor, _length - i);
        _length -= removed;
        return EditOutcome.Text(_cursor, _cursor, movedCursor: false);
    }

    /// <summary>Absolute caret seek clamped to [0, Length] — markdown helpers anchor here.</summary>
    public EditOutcome MoveTo(int offset)
    {
        if (_cursor == offset)
        {
            return EditOutcome.Unchanged;
        }

        _cursor = Math.Clamp(offset, 0, _length);
        return EditOutcome.Cursor();
    }

    /// <summary>
    ///     Removes <paramref name="count" /> chars at <paramref name="start" /> in one
    ///     array shift (markdown toggle unwrap needs non-caret spans). Char-index based:
    ///     callers must pass rune-aligned bounds.
    /// </summary>
    internal EditOutcome RemoveRange(int start, int count)
    {
        if (count <= 0 || start < 0 || start >= _length)
        {
            return EditOutcome.Unchanged;
        }

        Checkpoint();
        count = Math.Min(count, _length - start);
        Array.Copy(_buf, start + count, _buf, start, _length - start - count);
        _length -= count;
        if (_cursor > start)
        {
            _cursor = Math.Max(start, _cursor - count);
        }

        return EditOutcome.Text(start, start, movedCursor: true);
    }

    // ── Undo/redo ──────────────────────────────────────────────────────────

    /// <summary>Upper bound on remembered edit steps (bounded memory for long drafts).</summary>
    public const int MaxUndoSteps = 128;

    private readonly List<UndoPoint> _undo = [];
    private readonly List<UndoPoint> _redo = [];

    private readonly record struct UndoPoint(string Text, int Cursor);

    /// <summary>Steps back one effective text change; no-op when no checkpoints exist.</summary>
    public EditOutcome Undo()
    {
        if (_undo.Count == 0)
        {
            return EditOutcome.Unchanged;
        }

        PushCurrent(_redo);
        var target = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        Restore(target);
        return EditOutcome.Text(0, _length, movedCursor: true);
    }

    /// <summary>Re-applies the most recent undone change; no-op when the redo stack is empty.</summary>
    public EditOutcome Redo()
    {
        if (_redo.Count == 0)
        {
            return EditOutcome.Unchanged;
        }

        PushCurrent(_undo);
        var target = _redo[^1];
        _redo.RemoveAt(_redo.Count - 1);
        Restore(target);
        return EditOutcome.Text(0, _length, movedCursor: true);
    }

    /// <summary>
    ///     Pre-mutation snapshot hook for every effective text edit. Records the
    ///     pre-edit state on the undo stack and invalidates redo history — any
    ///     new edit forks a fresh timeline.
    /// </summary>
    private void Checkpoint()
    {
        PushCurrent(_undo);
        _redo.Clear();
    }

    private void PushCurrent(List<UndoPoint> stack)
    {
        if (stack.Count == MaxUndoSteps)
        {
            stack.RemoveAt(0);
        }

        stack.Add(new UndoPoint(SnapshotText(), _cursor));
    }

    private void Restore(UndoPoint point)
    {
        EnsureCapacity(point.Text.Length);
        point.Text.AsSpan().CopyTo(_buf);
        _length = point.Text.Length;
        _cursor = Math.Clamp(point.Cursor, 0, _length);
    }

    private void PurgeHistory()
    {
        _undo.Clear();
        _redo.Clear();
    }

    public EditOutcome MoveLeft()
    {
        if (_cursor == 0)
        {
            return EditOutcome.Unchanged;
        }

        _cursor = PrevRuneBoundary(_cursor);
        return EditOutcome.Cursor();
    }

    public EditOutcome MoveRight()
    {
        if (_cursor >= _length)
        {
            return EditOutcome.Unchanged;
        }

        _cursor = NextRuneBoundary(_cursor);
        return EditOutcome.Cursor();
    }

    public EditOutcome MoveToLineStart() { _cursor = LineStartOf(_cursor); return EditOutcome.Cursor(); }
    public EditOutcome MoveToLineEnd() { _cursor = LineEndOf(_cursor); return EditOutcome.Cursor(); }
    public EditOutcome MoveToStart() { _cursor = 0; return EditOutcome.Cursor(); }
    public EditOutcome MoveToEnd() { _cursor = _length; return EditOutcome.Cursor(); }

    /// <summary>Alt+B / Ctrl+Left: jump to the start of the word before the caret.</summary>
    public EditOutcome MoveWordLeft()
    {
        int i = _cursor;
        while (i > 0 && char.IsWhiteSpace(_buf[i - 1]))
        {
            i--;
        }

        while (i > 0 && !char.IsWhiteSpace(_buf[i - 1]))
        {
            i--;
        }

        if (i == _cursor)
        {
            return EditOutcome.Unchanged;
        }

        _cursor = i;
        return EditOutcome.Cursor();
    }

    /// <summary>Alt+F / Ctrl+Right: jump past the whitespace run and the word after the caret.</summary>
    public EditOutcome MoveWordRight()
    {
        int i = _cursor;
        while (i < _length && char.IsWhiteSpace(_buf[i]))
        {
            i++;
        }

        while (i < _length && !char.IsWhiteSpace(_buf[i]))
        {
            i++;
        }

        if (i == _cursor)
        {
            return EditOutcome.Unchanged;
        }

        _cursor = i;
        return EditOutcome.Cursor();
    }

    /// <summary>Up arrow: previous logical line at the same display column.</summary>
    public EditOutcome MoveUp()
    {
        int lineStart = LineStartOf(_cursor);
        if (lineStart == 0)
        {
            _cursor = 0;
            return EditOutcome.Cursor();
        }

        int columnCells = DisplayCells(_buf.AsSpan(lineStart, _cursor - lineStart));
        int prevStart = LineStartOf(lineStart - 1);
        int prevEnd = lineStart - 1; // index of '\n'
        _cursor = ClampToCells(prevStart, prevEnd, prevStart, columnCells);
        return EditOutcome.Cursor();
    }

    /// <summary>Down arrow: next logical line at the same display column.</summary>
    public EditOutcome MoveDown()
    {
        int lineEnd = LineEndOf(_cursor);
        if (lineEnd >= _length)
        {
            _cursor = _length;
            return EditOutcome.Cursor();
        }

        int lineStart = LineStartOf(_cursor);
        int columnCells = DisplayCells(_buf.AsSpan(lineStart, _cursor - lineStart));
        int nextStart = lineEnd + 1;
        int nextEnd = LineEndOf(nextStart);
        _cursor = ClampToCells(nextStart, nextEnd, nextStart, columnCells);
        return EditOutcome.Cursor();
    }

    // ── Geometry helpers ───────────────────────────────────────────────────

    /// <summary>Zero-based index of the logical line containing char offset <paramref name="offset"/>.</summary>
    public int LineIndexOf(int offset)
    {
        int line = 0;
        for (int i = 0; i < offset && i < _length; i++)
        {
            if (_buf[i] == '\n')
            {
                line++;
            }
        }

        return line;
    }

    public int LineStartOf(int offset)
    {
        int i = Math.Min(offset, _length) - 1;
        while (i >= 0 && _buf[i] != '\n')
        {
            i--;
        }

        return i + 1;
    }

    public int LineEndOf(int offset)
    {
        int i = Math.Max(offset, 0);
        while (i < _length && _buf[i] != '\n')
        {
            i++;
        }

        return i;
    }

    /// <summary>Display-cell span of a slice (surrogate pairs decode to one rune).</summary>
    internal static int DisplayCells(ReadOnlySpan<char> slice)
    {
        int cells = 0;
        var rest = slice;
        while (!rest.IsEmpty)
        {
            if (Rune.DecodeFromUtf16(rest, out var rune, out int consumed) == OperationStatus.Done)
            {
                cells += UnicodeWidth.Width(rune);
                rest = rest[consumed..];
            }
            else
            {
                cells += 1;
                rest = rest[1..];
            }
        }

        return cells;
    }

    /// <summary>Offset of the rune boundary at or after <paramref name="start"/>
    /// where accumulated display cells reach <paramref name="cells"/>.</summary>
    internal int ClampToCells(int start, int end, int fallback, int cells)
    {
        int cur = start;
        int acc = 0;
        while (cur < end)
        {
            if (acc >= cells)
            {
                return cur;
            }

            if (Rune.DecodeFromUtf16(_buf.AsSpan(cur, end - cur), out var rune, out int consumed) != OperationStatus.Done)
            {
                consumed = 1;
            }

            acc += UnicodeWidth.Width(rune);
            cur += consumed;
            if (acc > cells)
            {
                return cur - consumed;
            }
        }

        return end;
    }

    private int PrevRuneBoundary(int index)
    {
        int i = index - 1;
        if (i > 0 && char.IsLowSurrogate(_buf[i]) && char.IsHighSurrogate(_buf[i - 1]))
        {
            i--;
        }

        return i;
    }

    private int NextRuneBoundary(int index)
    {
        int i = index + 1;
        if (i < _length && char.IsHighSurrogate(_buf[index]) && char.IsLowSurrogate(_buf[index + 1]))
        {
            i++;
        }

        return Math.Min(i, _length);
    }

    private void EnsureCapacity(int needed)
    {
        if (needed <= _buf.Length)
        {
            return;
        }

        int target = _buf.Length;
        while (target < needed)
        {
            target *= 2;
        }

        Array.Resize(ref _buf, target);
    }
}
