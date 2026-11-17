using Harbor.Tui.CellForge.Input;
using Harbor.Tui.CellForge.Rendering;
using Harbor.Tui.CellForge.Widgets;

namespace Harbor.Tui.CellForge.Generic.Widgets;

/// <summary>
/// Which kind of selection a <see cref="CopyMode"/> session is extending.
/// Mirrors the v/V pair from vim and tmux copy mode: character-wise or
/// full-line.
/// </summary>
public enum CopySelectionKind
{
    /// <summary>No selection anchored yet.</summary>
    None,

    /// <summary>Character-wise selection, anchored at the cursor (v).</summary>
    Character,

    /// <summary>Line-wise selection, spanning whole rows (V).</summary>
    Line,
}

/// <summary>
/// Result of feeding a key to <see cref="CopyMode.OnKey"/>. A key is either
/// ignored (the host keeps routing it), handled (mode changed, repaint needed),
/// or copied (clipboard sequence the host must write to its backend).
/// </summary>
/// <param name="Claimed">True when copy mode consumed the key.</param>
/// <param name="ClipboardSequence">OSC 52 sequence when a copy was committed.</param>
public readonly record struct CopyKeyResult(bool Claimed, string? ClipboardSequence)
{
    /// <summary>The key did not belong to copy mode.</summary>
    public static CopyKeyResult Ignored => new(false, null);

    /// <summary>The key changed copy mode, no clipboard write needed.</summary>
    public static CopyKeyResult Handled => new(true, null);

    /// <summary>A selection was copied; write <paramref name="sequence"/> to the backend.</summary>
    public static CopyKeyResult Copied(string sequence) => new(true, sequence);
}

/// <summary>
/// Generalized CellForge copy primitive (issue #2322): a visible cell
/// selection driven by both mouse and keyboard, copied through OSC 52.
/// Domain-free. It reuses <see cref="SelectionEngine"/> for the rectangle,
/// text extraction (wide-tail skipping, trailing-whitespace trim) and
/// Reverse painting, and <see cref="Osc52Clipboard"/> for the clipboard
/// transport. Mouse input is delegated verbatim; keyboard input adds a
/// v/V-like navigation shell around the same engine.
/// </summary>
/// <remarks>
/// Key map while active: v / V anchor a character or line selection; h/j/k/l
/// and the arrow keys move the cursor; 0/$ and Home/End jump within a row;
/// y or Enter copies and exits; Escape or q cancels. Space anchors a character
/// selection (tmux style). All character commands require no modifiers.
/// </remarks>
public sealed class CopyMode
{
    private readonly SelectionEngine _selection = new();
    private (int X, int Y) _cursor;
    private (int X, int Y)? _anchor;
    private CopySelectionKind _kind;
    private bool _active;

    /// <summary>True while copy mode is active (keyboard session running).</summary>
    public bool IsActive => _active;

    /// <summary>True when a keyboard selection has been anchored.</summary>
    public bool HasSelection => _anchor is not null;

    /// <summary>Kind of the active keyboard selection.</summary>
    public CopySelectionKind Kind => _kind;

    /// <summary>Current cursor cell in the live buffer.</summary>
    public (int X, int Y) Cursor => _cursor;

    /// <summary>Enters copy mode with the cursor at the given cell, no selection yet.</summary>
    public void Enter(int cursorX, int cursorY)
    {
        _active = true;
        _cursor = (cursorX, cursorY);
        _anchor = null;
        _kind = CopySelectionKind.None;
        _selection.Clear();
    }

    /// <summary>Leaves copy mode and drops any selection.</summary>
    public void Exit()
    {
        _active = false;
        _anchor = null;
        _kind = CopySelectionKind.None;
        _selection.Clear();
    }

    /// <summary>Alias for <see cref="Exit"/>; safe to call on any state change.</summary>
    public void Clear() => Exit();

    /// <summary>
    /// Feeds a key to copy mode. Inactive: v/V/space enter and anchor a
    /// selection. Active: navigation, re-anchoring, copy and cancel. Returns
    /// whether the key was claimed and, on copy, the sequence to write.
    /// </summary>
    public CopyKeyResult OnKey(KeyEvent key, int cols, int rows, Func<int, int, Cell> cellAt)
    {
        int maxX = Math.Max(0, cols - 1);
        int maxY = Math.Max(0, rows - 1);
        // A capital V arrives as the 'V' rune plus Shift, so Shift is admitted;
        // only the command modifiers (Ctrl/Alt/Meta) disqualify a char command.
        bool plain = (key.Modifiers & (KeyModifiers.Ctrl | KeyModifiers.Alt | KeyModifiers.Meta)) == 0;
        bool IsChar(char c) => plain && key.Key == KeyCode.Char && key.Character.Value == c;

        if (!_active)
        {
            if (IsChar('v') || IsChar(' '))
            {
                Enter(0, 0);
                Anchor(CopySelectionKind.Character, cols);
                return CopyKeyResult.Handled;
            }

            if (IsChar('V'))
            {
                Enter(0, 0);
                Anchor(CopySelectionKind.Line, cols);
                return CopyKeyResult.Handled;
            }

            return CopyKeyResult.Ignored;
        }

        if (key.Key == KeyCode.Escape || IsChar('q'))
        {
            Exit();
            return CopyKeyResult.Handled;
        }

        if (IsChar('v') || IsChar(' '))
        {
            Anchor(CopySelectionKind.Character, cols);
            return CopyKeyResult.Handled;
        }

        if (IsChar('V'))
        {
            Anchor(CopySelectionKind.Line, cols);
            return CopyKeyResult.Handled;
        }

        if (IsChar('h') || key.Key == KeyCode.Left)
        {
            Move(-1, 0, maxX, maxY, cols);
            return CopyKeyResult.Handled;
        }

        if (IsChar('l') || key.Key == KeyCode.Right)
        {
            Move(1, 0, maxX, maxY, cols);
            return CopyKeyResult.Handled;
        }

        if (IsChar('k') || key.Key == KeyCode.Up)
        {
            Move(0, -1, maxX, maxY, cols);
            return CopyKeyResult.Handled;
        }

        if (IsChar('j') || key.Key == KeyCode.Down)
        {
            Move(0, 1, maxX, maxY, cols);
            return CopyKeyResult.Handled;
        }

        if (IsChar('0') || key.Key == KeyCode.Home)
        {
            _cursor = (0, _cursor.Y);
            Sync(cols);
            return CopyKeyResult.Handled;
        }

        if (IsChar('$') || key.Key == KeyCode.End)
        {
            _cursor = (maxX, _cursor.Y);
            Sync(cols);
            return CopyKeyResult.Handled;
        }

        if (key.Key == KeyCode.Enter || IsChar('y'))
        {
            string? sequence = Copy(cols, rows, cellAt);
            return sequence is null ? CopyKeyResult.Handled : CopyKeyResult.Copied(sequence);
        }

        return CopyKeyResult.Ignored;
    }

    /// <summary>
    /// Commits the current keyboard selection, returns the OSC 52 sequence to
    /// write (or null when nothing was selected), and leaves copy mode.
    /// </summary>
    public string? Copy(int cols, int rows, Func<int, int, Cell> cellAt)
    {
        if (!_active || _anchor is null)
        {
            Exit();
            return null;
        }

        int releaseX = _kind == CopySelectionKind.Line ? Math.Max(0, cols - 1) : _cursor.X;
        string? text = _selection.OnRelease(releaseX, _cursor.Y, cols, rows, cellAt);
        Exit();
        return string.IsNullOrEmpty(text) ? null : Osc52Clipboard.Encode(text);
    }

    /// <summary>Left press anchors the mouse selection. Other buttons pass through.</summary>
    public bool OnPress(int x, int y, MouseButton button) => _selection.OnPress(x, y, button);

    /// <summary>Drag extends the mouse selection.</summary>
    public void OnDrag(int x, int y) => _selection.OnDrag(x, y);

    /// <summary>
    /// Release extracts the mouse selection and returns its OSC 52 sequence,
    /// or null for a plain click or an empty region.
    /// </summary>
    public string? OnRelease(int x, int y, int cols, int rows, Func<int, int, Cell> cellAt)
    {
        string? text = _selection.OnRelease(x, y, cols, rows, cellAt);
        return string.IsNullOrEmpty(text) ? null : Osc52Clipboard.Encode(text);
    }

    /// <summary>Normalized selection rectangle, or null while nothing is selected.</summary>
    public Rect? SelectionRect(int cols, int rows) => _selection.NormalizedRect(cols, rows);

    /// <summary>
    /// Paints the visible selection with Reverse, plus the bare cursor cell
    /// while no selection is anchored so navigation is always visible.
    /// </summary>
    public void Paint(ScreenBuffer buffer)
    {
        if (_active && _anchor is null && buffer.Cols > 0 && buffer.Rows > 0)
        {
            int x = Math.Clamp(_cursor.X, 0, buffer.Cols - 1);
            int y = Math.Clamp(_cursor.Y, 0, buffer.Rows - 1);
            ReverseCell(buffer, x, y);
        }

        _selection.Paint(buffer);
    }

    private void Anchor(CopySelectionKind kind, int cols)
    {
        _kind = kind;
        _anchor = kind == CopySelectionKind.Line ? (0, _cursor.Y) : _cursor;
        Sync(cols);
    }

    private void Move(int dx, int dy, int maxX, int maxY, int cols)
    {
        _cursor = (
            Math.Clamp(_cursor.X + dx, 0, maxX),
            Math.Clamp(_cursor.Y + dy, 0, maxY));
        Sync(cols);
    }

    private void Sync(int cols)
    {
        if (_anchor is not { } anchor)
        {
            _selection.Clear();
            return;
        }

        _selection.OnPress(anchor.X, anchor.Y, MouseButton.Left);
        int maxX = Math.Max(0, cols - 1);
        int extentX = _kind == CopySelectionKind.Line ? maxX : Math.Clamp(_cursor.X, 0, maxX);
        _selection.OnDrag(extentX, _cursor.Y);
    }

    private static void ReverseCell(ScreenBuffer buffer, int x, int y)
    {
        var cellStyle = buffer.Get(x, y).Style;
        var style = new CellStyle(cellStyle.Fg, cellStyle.Bg, cellStyle.Attrs | StyleAttr.Reverse);
        buffer.SetStyleAt(x, y, in style);
    }
}
