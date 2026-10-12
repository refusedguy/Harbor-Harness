namespace Harbor.Tui.CellForge.Generic.Widgets;

/// <summary>
/// Generic option list over cells (Termina <c>SelectionList</c> / RazorConsole
/// <c>Select</c> slot, epic #1195): clamped selection, palette-style motion
/// and a proportional scrollbar, with no chat coupling. The dialog select
/// list stays the chat-scoped primitive; this is the neutral one future
/// generic dialogs compose from.
/// </summary>
public sealed class GenericSelectionList : IGenericFormField
{
    private readonly List<string> _items = new();
    private int _selected;
    private int _offset;

    /// <summary>Field label painted by <see cref="GenericForm"/>; empty draws no label.</summary>
    public string Label { get; set; } = string.Empty;

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

    /// <summary>When true, an empty selection fails <see cref="Validate"/>.</summary>
    public bool Required { get; set; }

    /// <summary>Maximum painted rows; taller lists scroll. At least 1.</summary>
    public int MaxVisibleRows { get; set; } = 6;

    /// <inheritdoc />
    public int DesiredHeight => Math.Max(1, Math.Min(Count, Math.Max(1, MaxVisibleRows)));

    /// <summary>Replaces the options (copied; null entries coerce to empty).</summary>
    public void SetItems(IReadOnlyList<string> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        _items.Clear();
        for (int i = 0; i < items.Count; i++)
        {
            _items.Add(items[i] ?? string.Empty);
        }

        _selected = 0;
        _offset = 0;
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

    /// <summary>Moves the selection by a signed delta (clamped, no wrap).</summary>
    public void Move(int delta)
    {
        if (_items.Count == 0 || delta == 0)
        {
            return;
        }

        _selected = Math.Clamp(_selected + delta, 0, _items.Count - 1);
        EnsureVisible();
    }

    /// <summary>Handles list motion; Enter/Escape/Tab stay with the host.</summary>
    /// <returns>True when consumed.</returns>
    public bool HandleKey(in KeyEvent key)
    {
        if (key.EventType == KeyEventType.Release || _items.Count == 0)
        {
            return false;
        }

        switch (key.Key)
        {
            case KeyCode.Up:
                Move(-1);
                return true;
            case KeyCode.Down:
                Move(1);
                return true;
            case KeyCode.Home:
                MoveTo(0);
                return true;
            case KeyCode.End:
                MoveTo(_items.Count - 1);
                return true;
            case KeyCode.PageUp:
                Move(-Math.Max(1, DesiredHeight));
                return true;
            case KeyCode.PageDown:
                Move(Math.Max(1, DesiredHeight));
                return true;
            default:
                return false;
        }
    }

    /// <inheritdoc />
    public string? Validate() =>
        Required && _items.Count == 0 ? "Choose an option." : null;

    /// <summary>First visible row (for tests and scrollbar sync).</summary>
    public int Offset => _offset;

    /// <summary>Paints up to <see cref="DesiredHeight"/> rows; selected row is reversed.</summary>
    public void Paint(ScreenBuffer buffer, Rect rect)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        if (rect.Width <= 0 || rect.Height <= 0 || _items.Count == 0)
        {
            return;
        }

        int rows = Math.Min(rect.Height, DesiredHeight);
        SyncTo(rows);

        var rowStyle = new CellStyle(ChatPalette.Text);
        var selectedStyle = new CellStyle(ChatPalette.Text, attrs: StyleAttr.Reverse);
        var markerStyle = new CellStyle(ChatPalette.Accent, attrs: StyleAttr.Bold);
        bool scrollable = _items.Count > rows;
        int textWidth = scrollable ? rect.Width - 1 : rect.Width;

        for (int r = 0; r < rows; r++)
        {
            int index = _offset + r;
            if (index >= _items.Count)
            {
                break;
            }

            int y = rect.Y + r;
            bool selected = index == SelectedIndex;
            string item = _items[index];
            string row = selected ? "> " + item : "  " + item;
            buffer.SetText(rect.X, y, row.AsSpan(0, Math.Min(row.Length, Math.Max(0, textWidth))), selected ? selectedStyle : rowStyle);
            if (selected && textWidth > 0)
            {
                buffer.SetStyleAt(rect.X, y, markerStyle);
            }
        }

        if (scrollable && rect.Width >= 2)
        {
            PaintScrollbar(buffer, new Rect(rect.X + rect.Width - 1, rect.Y, 1, rows), rows);
        }
    }

    private void SyncTo(int visibleRows)
    {
        int maxOffset = Math.Max(0, _items.Count - Math.Max(1, visibleRows));
        _offset = Math.Clamp(_offset, 0, maxOffset);
        EnsureVisible();
    }

    private void EnsureVisible()
    {
        int rows = Math.Max(1, DesiredHeight);
        int target = Math.Clamp(_selected, 0, _items.Count - 1);
        if (target < _offset)
        {
            _offset = target;
        }
        else if (target >= _offset + rows)
        {
            _offset = target - rows + 1;
        }
    }

    private void PaintScrollbar(ScreenBuffer buffer, Rect rect, int visibleRows)
    {
        int total = _items.Count;
        int maxOffset = Math.Max(1, total - visibleRows);
        int thumbLen = Math.Max(1, (int)Math.Round((double)visibleRows * rect.Height / total, MidpointRounding.AwayFromZero));
        thumbLen = Math.Min(thumbLen, rect.Height);
        int thumbStart = (int)Math.Round((double)_offset * (rect.Height - thumbLen) / maxOffset, MidpointRounding.AwayFromZero);
        var trackStyle = new CellStyle(ChatPalette.Border);
        var thumbStyle = new CellStyle(ChatPalette.Muted);

        for (int r = 0; r < rect.Height; r++)
        {
            bool thumb = r >= thumbStart && r < thumbStart + thumbLen;
            buffer.SetText(rect.X, rect.Y + r, thumb ? "█" : "│", thumb ? thumbStyle : trackStyle);
        }
    }
}
