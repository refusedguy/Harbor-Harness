using System.Text;
using Harbor.Tui.CellForge.Rendering;

namespace Harbor.Tui.CellForge.Widgets;

/// <summary>
/// SelectList form primitive ([PRIM4], part of #289): single-column option
/// list with palette-style keyboard navigation (Up/Down/PageUp/PageDown/
/// Home/End, clamped — the <see cref="CommandPaletteView"/> list behavior
/// minus the fuzzy query; dialog option sets are enumerated, not searched).
/// Scroll state lives in <see cref="ScrollableViewport"/>; the host syncs it
/// to the painted rows via <see cref="SyncViewport"/> (the same
/// Configure+EnsureVisible pattern the palette uses since [PRIM3c]) and paints
/// a <see cref="Scrollbar"/> overlay while overflowing. UI-only state —
/// rendering stays in <see cref="DialogOverlay"/>.
/// </summary>
public sealed class DialogSelectList
{
    private readonly List<string> _items = new();
    private readonly ScrollableViewport _viewport = new();
    private int _selected;

    /// <summary>Options in host order (defensive copy).</summary>
    public IReadOnlyList<string> Items => _items;

    /// <summary>Number of options.</summary>
    public int Count => _items.Count;

    /// <summary>Selected index (clamped; -1 when empty).</summary>
    public int SelectedIndex => _items.Count == 0 ? -1 : Math.Clamp(_selected, 0, _items.Count - 1);

    /// <summary>Selected option (empty when none).</summary>
    public string SelectedItem
    {
        get
        {
            int i = SelectedIndex;
            return i < 0 ? string.Empty : _items[i];
        }
    }

    /// <summary>Scroll viewport over the option rows.</summary>
    public ScrollableViewport Viewport => _viewport;

    /// <summary>Replaces the options (copied; null entries coerce to empty) and resets selection/scroll.</summary>
    public void SetItems(IReadOnlyList<string> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        _items.Clear();
        for (int i = 0; i < items.Count; i++)
        {
            _items.Add(items[i] ?? string.Empty);
        }
        _selected = 0;
        _viewport.SetTotal(_items.Count);
        _viewport.SetOffset(0);
    }

    /// <summary>Selects an index (clamped). No-op when empty.</summary>
    public void MoveTo(int index)
    {
        if (_items.Count == 0)
        {
            return;
        }
        _selected = Math.Clamp(index, 0, _items.Count - 1);
        EnsureVisible();
    }

    /// <summary>Moves the selection by a signed delta (clamped, palette-style — no wrap).</summary>
    public void Move(int delta)
    {
        if (_items.Count == 0 || delta == 0)
        {
            return;
        }
        _selected = Math.Clamp(_selected + delta, 0, _items.Count - 1);
        EnsureVisible();
    }

    /// <summary>
    /// Syncs the viewport to <paramref name="visibleRows"/> painted rows.
    /// Call from Paint before slicing (the palette does the same).
    /// </summary>
    public void SyncViewport(int visibleRows)
    {
        _viewport.Configure(_items.Count, Math.Max(0, visibleRows));
        EnsureVisible();
    }

    private void EnsureVisible()
    {
        if (_items.Count == 0 || _viewport.ViewportH <= 0)
        {
            return;
        }
        int target = Math.Clamp(_selected, 0, _items.Count - 1);
        if (target < _viewport.Offset)
        {
            _viewport.SetOffset(target);
        }
        else if (target >= _viewport.Offset + _viewport.ViewportH)
        {
            _viewport.SetOffset(target - _viewport.ViewportH + 1);
        }
    }
}

/// <summary>
/// Radio form primitive ([PRIM4], part of #289): single-row mutually-exclusive
/// option set (the dialog "radio row"). Left/Right (Up/Down are aliases) move
/// with wrap-around — the same cycling idiom dialog buttons use; Enter commits
/// via the host, which reads <see cref="SelectedOption"/>. UI-only state.
/// </summary>
public sealed class DialogRadioGroup
{
    private readonly List<string> _options = new();
    private int _selected;

    /// <summary>Options in host order (defensive copy).</summary>
    public IReadOnlyList<string> Options => _options;

    /// <summary>Number of options.</summary>
    public int Count => _options.Count;

    /// <summary>Selected index (normalized; -1 when empty).</summary>
    public int SelectedIndex => _options.Count == 0 ? -1 : _selected;

    /// <summary>Selected option (empty when none).</summary>
    public string SelectedOption
    {
        get
        {
            int i = SelectedIndex;
            return i < 0 ? string.Empty : _options[i];
        }
    }

    /// <summary>Replaces the options (copied; null entries coerce to empty) and selects <paramref name="selectedIndex"/> (clamped).</summary>
    public void SetOptions(IReadOnlyList<string> options, int selectedIndex = 0)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options.Clear();
        for (int i = 0; i < options.Count; i++)
        {
            _options.Add(options[i] ?? string.Empty);
        }
        _selected = _options.Count == 0 ? 0 : Math.Clamp(selectedIndex, 0, _options.Count - 1);
    }

    /// <summary>Moves the selection by a signed delta, wrapping around. No-op when empty.</summary>
    public void Move(int delta)
    {
        if (_options.Count == 0 || delta == 0)
        {
            return;
        }
        int n = _options.Count;
        _selected = ((_selected + delta) % n + n) % n;
    }

    /// <summary>Selects an index (clamped). No-op when empty.</summary>
    public void MoveTo(int index)
    {
        if (_options.Count == 0)
        {
            return;
        }
        _selected = Math.Clamp(index, 0, _options.Count - 1);
    }
}

/// <summary>
/// Multiline TextInput form primitive ([PRIM4], part of #289): the
/// <see cref="PromptBuffer"/> editor (same engine as the composer) behind a
/// dialog-owned facade. Holds embedded newlines (multiline prefill, paste,
/// kitty Shift+Enter); plain Enter still submits — the uniform dialog contract
/// (hosts own the commit gesture, as <c>OnboardingFlow</c> does) — while cursor
/// keys navigate lines. UI-only state.
/// </summary>
public sealed class DialogMultilineInput
{
    private readonly PromptBuffer _buffer = new();
    private char? _pendingHighSurrogate;

    /// <summary>Current text (may contain embedded newlines).</summary>
    public string Text => _buffer.SnapshotText();

    /// <summary>Number of logical lines (1 + embedded newlines).</summary>
    public int LineCount => _buffer.LineCount;

    /// <summary>UTF-16 caret offset.</summary>
    public int Cursor => _buffer.Cursor;

    /// <summary>Replaces the text; the caret parks at the end (prefill semantics).</summary>
    public void SetText(string text)
    {
        _buffer.Clear();
        _pendingHighSurrogate = null;
        if (!string.IsNullOrEmpty(text))
        {
            _ = _buffer.InsertText(text);
        }
    }

    /// <summary>
    /// Inserts a char at the caret, recombining console-split surrogate halves
    /// (legacy consoles deliver astral chars as two key events). Lone halves
    /// are dropped — never crash, never corrupt.
    /// </summary>
    public void InsertChar(char c)
    {
        if (char.IsHighSurrogate(c))
        {
            _pendingHighSurrogate = c;
            return;
        }
        if (char.IsLowSurrogate(c))
        {
            if (_pendingHighSurrogate is char hi)
            {
                _pendingHighSurrogate = null;
                _ = _buffer.Insert(new Rune(hi, c));
            }
            return;
        }
        _pendingHighSurrogate = null;
        _ = _buffer.Insert(new Rune(c));
    }

    /// <summary>Inserts a newline at the caret (kitty Shift+Enter path).</summary>
    public void InsertNewline()
    {
        _pendingHighSurrogate = null;
        _ = _buffer.Insert(new Rune('\n'));
    }

    /// <summary>Deletes the rune before the caret.</summary>
    public void Backspace()
    {
        _pendingHighSurrogate = null;
        _ = _buffer.Backspace();
    }

    /// <summary>Deletes the rune under the caret.</summary>
    public void DeleteForward()
    {
        _pendingHighSurrogate = null;
        _ = _buffer.DeleteForward();
    }

    /// <summary>Moves the caret one rune left.</summary>
    public void MoveLeft()
    {
        _pendingHighSurrogate = null;
        _ = _buffer.MoveLeft();
    }

    /// <summary>Moves the caret one rune right.</summary>
    public void MoveRight()
    {
        _pendingHighSurrogate = null;
        _ = _buffer.MoveRight();
    }

    /// <summary>Moves the caret one visual line up (column-preserving).</summary>
    public void MoveUp()
    {
        _pendingHighSurrogate = null;
        _ = _buffer.MoveUp();
    }

    /// <summary>Moves the caret one visual line down (column-preserving).</summary>
    public void MoveDown()
    {
        _pendingHighSurrogate = null;
        _ = _buffer.MoveDown();
    }

    /// <summary>Moves the caret to the current line start.</summary>
    public void MoveToLineStart()
    {
        _pendingHighSurrogate = null;
        _ = _buffer.MoveToLineStart();
    }

    /// <summary>Moves the caret to the current line end.</summary>
    public void MoveToLineEnd()
    {
        _pendingHighSurrogate = null;
        _ = _buffer.MoveToLineEnd();
    }
}
