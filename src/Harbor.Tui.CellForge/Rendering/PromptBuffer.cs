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
    TextAndCursor = 3
}

/// <summary>
///     Redraw hint produced by every <see cref="PromptBuffer" /> operation
///     (grok EditOutcome pattern): the view decides between full-line repaint and
///     cursor-only patching without comparing snapshots.
/// </summary>
public readonly record struct EditOutcome(EditOutcomeKind Kind, int TextStart, int TextEnd)
{
    public static readonly EditOutcome Unchanged = default;

    public static EditOutcome Cursor() => new(EditOutcomeKind.CursorOnly, 0, 0);

    public static EditOutcome Text(int start, int end, bool movedCursor) =>
        new(movedCursor ? EditOutcomeKind.TextAndCursor : EditOutcomeKind.TextOnly, start, end);
}

/// <summary>
///     Single-line-first prompt editor model: one growable char array + UTF-16
///     caret that never sits inside a surrogate pair. Shift+Enter inserts a
///     literal newline so the buffer can hold multiple logical lines; horizontal
///     overflow is handled by <see cref="PromptViewport.ScrollIntoView" />, not by
///     soft wrapping (WrapCache joins in CE-2 when the grid lands).
///     Allocation budget: movement/cursor ops are zero-alloc; text edits grow the
///     backing array geometrically; <see cref="SnapshotText" /> allocates on demand.
/// </summary>
public sealed class PromptBuffer
{
    private const int InitialCapacity = 256;

    // ── Undo/redo ──────────────────────────────────────────────────────────

    /// <summary>Upper bound on remembered edit steps (bounded memory for long drafts).</summary>
    public const int MaxUndoSteps = 128;
    private readonly List<UndoPoint> _redo = [];

    private readonly List<UndoPoint> _undo = [];

    private char[] _buf = new char[InitialCapacity];

    public int Length { get; private set; }
    public int Cursor { get; private set; }
    public bool IsEmpty => Length == 0;

    /// <summary>Number of logical lines (1 + count of embedded newlines).</summary>
    public int LineCount
    {
        get
        {
            int lines = 1;
            for (int i = 0; i < Length; i++)
            {
                if (_buf[i] == '\n')
                {
                    lines++;
                }
            }

            return lines;
        }
    }

    /// <summary>
    ///     Text of the last completed readline kill (Ctrl+U/W/K, Alt+D).
    ///     Backspace/DeleteForward are not kills; a no-op kill never clobbers the
    ///     previous entry. Single-slot kill ring backing the composer's Ctrl+Y yank.
    /// </summary>
    public string? LastKill { get; private set; }

    /// <summary>Copies current content into a string (submit path).</summary>
    public string SnapshotText() => new(_buf, 0, Length);

    /// <summary>
    ///     Live view over current content — zero-alloc read for the every-frame
    ///     composer paint. Valid until the next edit (edits may grow/reorder the
    ///     backing array); painters consume the span within the frame.
    /// </summary>
    public ReadOnlySpan<char> AsSpan() => _buf.AsSpan(0, Length);

    /// <summary>Takes the content and resets the buffer (Enter-submit).</summary>
    public string TakeText()
    {
        string text = SnapshotText();
        Length = 0;
        Cursor = 0;
        PurgeHistory();
        return text;
    }

    public void Clear()
    {
        Length = 0;
        Cursor = 0;
        PurgeHistory();
    }

    // ── Edits ──────────────────────────────────────────────────────────────

    public EditOutcome Insert(Rune rune)
    {
        Checkpoint();
        int size = rune.Utf16SequenceLength;
        EnsureCapacity(Length + size);
        Array.Copy(_buf, Cursor, _buf, Cursor + size, Length - Cursor);
        if (size == 1)
        {
            _buf[Cursor++] = (char)rune.Value;
        }
        else
        {
            Span<char> tmp = stackalloc char[2];
            _ = rune.EncodeToUtf16(tmp);
            tmp.CopyTo(_buf.AsSpan(Cursor));
            Cursor += 2;
        }

        Length += size;
        return EditOutcome.Text(Cursor - size, Cursor, true);
    }

    public EditOutcome InsertText(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return EditOutcome.Unchanged;
        }

        Checkpoint();
        EnsureCapacity(Length + text.Length);
        Array.Copy(_buf, Cursor, _buf, Cursor + text.Length, Length - Cursor);
        text.CopyTo(0, _buf, Cursor, text.Length);
        Cursor += text.Length;
        Length += text.Length;
        return EditOutcome.Text(Cursor - text.Length, Cursor, true);
    }

    public EditOutcome Backspace()
    {
        if (Cursor == 0)
        {
            return EditOutcome.Unchanged;
        }

        Checkpoint();
        int start = PrevRuneBoundary(Cursor);
        int removed = Cursor - start;
        Array.Copy(_buf, Cursor, _buf, start, Length - Cursor);
        Length -= removed;
        Cursor = start;
        return EditOutcome.Text(start, start, true);
    }

    public EditOutcome DeleteForward()
    {
        if (Cursor >= Length)
        {
            return EditOutcome.Unchanged;
        }

        Checkpoint();
        int end = NextRuneBoundary(Cursor);
        int removed = end - Cursor;
        Array.Copy(_buf, end, _buf, Cursor, Length - end);
        Length -= removed;
        return EditOutcome.Text(Cursor, Cursor, false);
    }

    /// <summary>Ctrl+U: remove everything before the caret on the current line.</summary>
    public EditOutcome DeleteToLineStart()
    {
        int lineStart = LineStartOf(Cursor);
        if (Cursor == lineStart)
        {
            return EditOutcome.Unchanged;
        }

        Checkpoint();
        int removed = Cursor - lineStart;
        LastKill = new string(_buf, lineStart, removed);
        Array.Copy(_buf, Cursor, _buf, lineStart, Length - Cursor);
        Length -= removed;
        Cursor = lineStart;
        return EditOutcome.Text(lineStart, lineStart, true);
    }

    /// <summary>Ctrl+W: remove the word before the caret (whitespace-delimited).</summary>
    public EditOutcome DeleteWordBackward()
    {
        if (Cursor == 0)
        {
            return EditOutcome.Unchanged;
        }

        int i = Cursor;
        while (i > 0 && char.IsWhiteSpace(_buf[i - 1]))
        {
            i--;
        }

        while (i > 0 && !char.IsWhiteSpace(_buf[i - 1]))
        {
            i--;
        }

        if (i == Cursor)
        {
            return EditOutcome.Unchanged;
        }

        Checkpoint();
        int removed = Cursor - i;
        LastKill = new string(_buf, i, removed);
        Array.Copy(_buf, Cursor, _buf, i, Length - Cursor);
        Length -= removed;
        Cursor = i;
        return EditOutcome.Text(i, i, true);
    }

    /// <summary>Ctrl+K: remove everything from the caret to the end of the current line.</summary>
    public EditOutcome DeleteToLineEnd()
    {
        int lineEnd = LineEndOf(Cursor);
        if (Cursor == lineEnd)
        {
            return EditOutcome.Unchanged;
        }

        Checkpoint();
        int removed = lineEnd - Cursor;
        LastKill = new string(_buf, Cursor, removed);
        Array.Copy(_buf, lineEnd, _buf, Cursor, Length - lineEnd);
        Length -= removed;
        return EditOutcome.Text(Cursor, Cursor, false);
    }

    /// <summary>
    ///     Kill-ring-free readline <c>M-d</c>: remove the word in front of the
    ///     caret up to the next whitespace run. At end-of-line this mirrors
    ///     <see cref="MoveWordRight" /> boundaries so <c>Alt+d</c>/<c>Alt+b</c>
    ///     agree on what "word" means.
    /// </summary>
    public EditOutcome DeleteWordForward()
    {
        if (Cursor >= Length)
        {
            return EditOutcome.Unchanged;
        }

        Checkpoint();
        int i = Cursor;
        while (i < Length && char.IsWhiteSpace(_buf[i]))
        {
            i++;
        }

        while (i < Length && !char.IsWhiteSpace(_buf[i]))
        {
            i++;
        }

        int removed = i - Cursor;
        LastKill = new string(_buf, Cursor, removed);
        Array.Copy(_buf, i, _buf, Cursor, Length - i);
        Length -= removed;
        return EditOutcome.Text(Cursor, Cursor, false);
    }

    /// <summary>Absolute caret seek clamped to [0, Length] — markdown helpers anchor here.</summary>
    public EditOutcome MoveTo(int offset)
    {
        if (Cursor == offset)
        {
            return EditOutcome.Unchanged;
        }

        Cursor = Math.Clamp(offset, 0, Length);
        return EditOutcome.Cursor();
    }

    /// <summary>
    ///     Removes <paramref name="count" /> chars at <paramref name="start" /> in one
    ///     array shift (markdown toggle unwrap needs non-caret spans). Char-index based:
    ///     callers must pass rune-aligned bounds.
    /// </summary>
    internal EditOutcome RemoveRange(int start, int count)
    {
        if (count <= 0 || start < 0 || start >= Length)
        {
            return EditOutcome.Unchanged;
        }

        Checkpoint();
        count = Math.Min(count, Length - start);
        Array.Copy(_buf, start + count, _buf, start, Length - start - count);
        Length -= count;
        if (Cursor > start)
        {
            Cursor = Math.Max(start, Cursor - count);
        }

        return EditOutcome.Text(start, start, true);
    }

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
        return EditOutcome.Text(0, Length, true);
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
        return EditOutcome.Text(0, Length, true);
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

        stack.Add(new UndoPoint(SnapshotText(), Cursor));
    }

    private void Restore(UndoPoint point)
    {
        EnsureCapacity(point.Text.Length);
        point.Text.AsSpan().CopyTo(_buf);
        Length = point.Text.Length;
        Cursor = Math.Clamp(point.Cursor, 0, Length);
    }

    private void PurgeHistory()
    {
        _undo.Clear();
        _redo.Clear();
    }

    public EditOutcome MoveLeft()
    {
        if (Cursor == 0)
        {
            return EditOutcome.Unchanged;
        }

        Cursor = PrevRuneBoundary(Cursor);
        return EditOutcome.Cursor();
    }

    public EditOutcome MoveRight()
    {
        if (Cursor >= Length)
        {
            return EditOutcome.Unchanged;
        }

        Cursor = NextRuneBoundary(Cursor);
        return EditOutcome.Cursor();
    }

    public EditOutcome MoveToLineStart()
    {
        Cursor = LineStartOf(Cursor);
        return EditOutcome.Cursor();
    }
    public EditOutcome MoveToLineEnd()
    {
        Cursor = LineEndOf(Cursor);
        return EditOutcome.Cursor();
    }
    public EditOutcome MoveToStart()
    {
        Cursor = 0;
        return EditOutcome.Cursor();
    }
    public EditOutcome MoveToEnd()
    {
        Cursor = Length;
        return EditOutcome.Cursor();
    }

    /// <summary>Alt+B / Ctrl+Left: jump to the start of the word before the caret.</summary>
    public EditOutcome MoveWordLeft()
    {
        int i = Cursor;
        while (i > 0 && char.IsWhiteSpace(_buf[i - 1]))
        {
            i--;
        }

        while (i > 0 && !char.IsWhiteSpace(_buf[i - 1]))
        {
            i--;
        }

        if (i == Cursor)
        {
            return EditOutcome.Unchanged;
        }

        Cursor = i;
        return EditOutcome.Cursor();
    }

    /// <summary>Alt+F / Ctrl+Right: jump past the whitespace run and the word after the caret.</summary>
    public EditOutcome MoveWordRight()
    {
        int i = Cursor;
        while (i < Length && char.IsWhiteSpace(_buf[i]))
        {
            i++;
        }

        while (i < Length && !char.IsWhiteSpace(_buf[i]))
        {
            i++;
        }

        if (i == Cursor)
        {
            return EditOutcome.Unchanged;
        }

        Cursor = i;
        return EditOutcome.Cursor();
    }

    /// <summary>Up arrow: previous logical line at the same display column.</summary>
    public EditOutcome MoveUp()
    {
        int lineStart = LineStartOf(Cursor);
        if (lineStart == 0)
        {
            Cursor = 0;
            return EditOutcome.Cursor();
        }

        int columnCells = DisplayCells(_buf.AsSpan(lineStart, Cursor - lineStart));
        int prevStart = LineStartOf(lineStart - 1);
        int prevEnd = lineStart - 1; // index of '\n'
        Cursor = ClampToCells(prevStart, prevEnd, prevStart, columnCells);
        return EditOutcome.Cursor();
    }

    /// <summary>Down arrow: next logical line at the same display column.</summary>
    public EditOutcome MoveDown()
    {
        int lineEnd = LineEndOf(Cursor);
        if (lineEnd >= Length)
        {
            Cursor = Length;
            return EditOutcome.Cursor();
        }

        int lineStart = LineStartOf(Cursor);
        int columnCells = DisplayCells(_buf.AsSpan(lineStart, Cursor - lineStart));
        int nextStart = lineEnd + 1;
        int nextEnd = LineEndOf(nextStart);
        Cursor = ClampToCells(nextStart, nextEnd, nextStart, columnCells);
        return EditOutcome.Cursor();
    }

    // ── Geometry helpers ───────────────────────────────────────────────────

    /// <summary>Zero-based index of the logical line containing char offset <paramref name="offset" />.</summary>
    public int LineIndexOf(int offset)
    {
        int line = 0;
        for (int i = 0; i < offset && i < Length; i++)
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
        int i = Math.Min(offset, Length) - 1;
        while (i >= 0 && _buf[i] != '\n')
        {
            i--;
        }

        return i + 1;
    }

    public int LineEndOf(int offset)
    {
        int i = Math.Max(offset, 0);
        while (i < Length && _buf[i] != '\n')
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

    /// <summary>
    ///     Offset of the rune boundary at or after <paramref name="start" />
    ///     where accumulated display cells reach <paramref name="cells" />.
    /// </summary>
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
        if (i < Length && char.IsHighSurrogate(_buf[index]) && char.IsLowSurrogate(_buf[index + 1]))
        {
            i++;
        }

        return Math.Min(i, Length);
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

    private readonly record struct UndoPoint(string Text, int Cursor);
}
