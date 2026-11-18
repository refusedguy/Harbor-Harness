// Issue #2377: keyboard-driven anchored menu. One flat visible-row projection
// over a tree of items, with one open submenu path. Hotkey slots are displayed
// in the right column and activated with Alt+mnemonic. Activation is a host
// callback that receives the item id; the menu owns no command execution, no
// fuzzy ranking and no history (that is CommandPalettePrimitive, #2321).

using System.Text;

namespace Harbor.Tui.CellForge.Generic.Widgets;

/// <summary>One menu entry. Children form a submenu; the host owns activation.</summary>
/// <param name="Id">Stable id passed to the activation callback.</param>
/// <param name="Label">Visible text.</param>
/// <param name="Hotkey">Right-column mnemonic (first character is the Alt binding).</param>
/// <param name="Disabled">Skipped by navigation and never activated.</param>
/// <param name="Children">Optional submenu entries.</param>
public sealed record MenuItem(
    string Id,
    string Label,
    string Hotkey = "",
    bool Disabled = false,
    IReadOnlyList<MenuItem>? Children = null);

/// <summary>One currently visible row, with its depth and submenu state.</summary>
/// <param name="Item">The entry this row shows.</param>
/// <param name="Depth">Zero at the root, one per open submenu.</param>
/// <param name="IsExpanded">True while this row's submenu is showing.</param>
/// <param name="Index">Index of the entry inside its parent list.</param>
public readonly record struct MenuRow(MenuItem Item, int Depth, bool IsExpanded, int Index);

/// <summary>
/// Anchored menu model and painter. Navigation is a command gate: arrows and
/// vim keys move, Right opens a submenu, Left closes one, Enter activates,
/// Escape closes a level (or the whole menu), and Alt+mnemonic activates a
/// hotkey slot. Shift and Ctrl/Meta chords are refused; there is no text
/// buffer here. Press and repeat only.
/// </summary>
public sealed class AnchoredMenu
{
    /// <summary>Bounds recursive submenu expansion so a cyclic item list cannot hang the painter.</summary>
    public const int MaxDepth = 8;

    private static readonly CellStyle SelectedStyle = new(attrs: StyleAttr.Bold);

    private readonly List<MenuItem> _items = new();
    private readonly List<MenuRow> _rows = new();
    private readonly List<int> _expanded = new();
    private readonly List<int> _returnTo = new();
    private readonly Action<string>? _onActivate;
    private int _selected;

    /// <summary>Creates a menu over <paramref name="items"/>.</summary>
    public AnchoredMenu(IEnumerable<MenuItem> items, Action<string>? onActivate = null)
    {
        ArgumentNullException.ThrowIfNull(items);
        _onActivate = onActivate;
        SetItems(items);
    }

    /// <summary>Root entries, in the order supplied.</summary>
    public IReadOnlyList<MenuItem> Items => _items;

    /// <summary>Visible rows: roots plus the entries of every open submenu.</summary>
    public IReadOnlyList<MenuRow> Rows => _rows;

    /// <summary>Index into <see cref="Rows"/>, or -1 while the menu is empty.</summary>
    public int SelectedIndex => _selected;

    /// <summary>Currently selected row, or null.</summary>
    public MenuRow? Selected => _selected >= 0 && _selected < _rows.Count ? _rows[_selected] : null;

    /// <summary>Depth of the open submenu path.</summary>
    public int SubmenuDepth => _expanded.Count;

    /// <summary>Whether the menu is showing.</summary>
    public bool IsOpen { get; private set; }

    /// <summary>Size the painter wants: one cell per border and row, plus the hotkey column.</summary>
    public OverlaySize ContentSize
    {
        get
        {
            int inner = 0;
            for (int i = 0; i < _rows.Count; i++)
            {
                var row = _rows[i];
                int need = 2 + (row.Depth * 2) + row.Item.Label.Length
                    + (row.Item.Children is { Count: > 0 } ? 2 : 0);
                string hotkey = row.Item.Hotkey;
                if (hotkey.Length > 0)
                {
                    need += hotkey.Length + 1;
                }

                if (need > inner)
                {
                    inner = need;
                }
            }

            return new OverlaySize(inner + 2, _rows.Count + 2);
        }
    }

    /// <summary>Replaces the entries and resets selection and submenu state.</summary>
    public void SetItems(IEnumerable<MenuItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        _items.Clear();
        foreach (var item in items)
        {
            ArgumentNullException.ThrowIfNull(item);
            _items.Add(item);
        }

        _expanded.Clear();
        _returnTo.Clear();
        _selected = 0;
        Rebuild();
    }

    /// <summary>Opens the menu at its first enabled row.</summary>
    public void Open()
    {
        _expanded.Clear();
        _returnTo.Clear();
        _selected = 0;
        SkipDisabledForward();
        IsOpen = true;
    }

    /// <summary>Closes the menu, keeping the current selection for the next open.</summary>
    public void Close() => IsOpen = false;

    /// <summary>Moves the selection by <paramref name="delta"/>, skipping disabled rows and wrapping.</summary>
    public void Move(int delta)
    {
        if (_rows.Count == 0)
        {
            _selected = -1;
            return;
        }

        int step = delta >= 0 ? 1 : -1;
        for (int n = 0; n < _rows.Count; n++)
        {
            _selected = ((_selected + step) % _rows.Count + _rows.Count) % _rows.Count;
            if (!_rows[_selected].Item.Disabled)
            {
                return;
            }
        }
    }

    /// <summary>
    /// Routes one key. Returns true when consumed; a pressed or repeated key
    /// with a command chord is left to the host.
    /// </summary>
    public bool HandleKey(in KeyEvent key)
    {
        if (key.EventType != KeyEventType.Press && key.EventType != KeyEventType.Repeat)
        {
            return false;
        }

        if (key.Key == KeyCode.Char && key.Modifiers == KeyModifiers.Alt)
        {
            return ActivateHotkey(key.Character);
        }

        if (!key.Modifiers.IsUnmodified())
        {
            return false;
        }

        switch (key.Key)
        {
            case KeyCode.Down:
                Move(1);
                return true;
            case KeyCode.Up:
                Move(-1);
                return true;
            case KeyCode.Char:
                if (key.Character.Value == 'j')
                {
                    Move(1);
                    return true;
                }

                if (key.Character.Value == 'k')
                {
                    Move(-1);
                    return true;
                }

                return false;
            case KeyCode.Home:
                _selected = -1;
                Move(1);
                return true;
            case KeyCode.End:
                _selected = -1;
                Move(-1);
                return true;
            case KeyCode.Right:
                return OpenSubmenu();
            case KeyCode.Left:
                return CloseSubmenu();
            case KeyCode.Enter:
                return Activate();
            case KeyCode.Escape:
                if (CloseSubmenu())
                {
                    return true;
                }

                Close();
                return true;
            default:
                return false;
        }
    }

    /// <summary>
    /// Paints the visible rows inside <paramref name="rect"/>: a one-cell
    /// border, a selection marker, an indent per submenu level, a submenu
    /// arrow and a right-aligned hotkey column. The footprint is cleared first
    /// so a sparse popup never leaks the screen behind it.
    /// </summary>
    public void Paint(ScreenBuffer buffer, Rect rect)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        if (rect.Width <= 0 || rect.Height <= 0)
        {
            return;
        }

        buffer.Fill(rect, Cell.Blank);
        int inner = rect.Width - 2;
        if (inner <= 0 || rect.Height < 2)
        {
            return;
        }

        DrawBorder(buffer, rect, rect.Y);
        DrawBorder(buffer, rect, rect.Bottom - 1);

        int count = Math.Min(_rows.Count, rect.Height - 2);
        for (int i = 0; i < count; i++)
        {
            int y = rect.Y + 1 + i;
            buffer.SetText(rect.X, y, "|", CellStyle.Plain);
            buffer.SetText(rect.Right - 1, y, "|", CellStyle.Plain);

            var row = _rows[i];
            string text = RowText(row, i == _selected, inner);
            buffer.SetText(rect.X + 1, y, text.AsSpan(0, Math.Min(text.Length, inner)),
                i == _selected ? SelectedStyle : CellStyle.Plain);
        }
    }

    private static void DrawBorder(ScreenBuffer buffer, Rect rect, int y)
    {
        buffer.SetText(rect.X, y, new string('-', rect.Width).AsSpan(), CellStyle.Plain);
        buffer.SetText(rect.X, y, "+", CellStyle.Plain);
        buffer.SetText(rect.Right - 1, y, "+", CellStyle.Plain);
    }

    private static string RowText(MenuRow row, bool selected, int inner)
    {
        string text = (selected ? "> " : "  ") + new string(' ', row.Depth * 2) + row.Item.Label
            + (row.Item.Children is { Count: > 0 } ? " >" : string.Empty);
        string hotkey = row.Item.Hotkey;
        if (hotkey.Length > 0 && inner >= text.Length + hotkey.Length + 1)
        {
            text = text.PadRight(inner - hotkey.Length) + hotkey;
        }

        return text;
    }

    private void Rebuild()
    {
        _rows.Clear();
        AddLevel(_items, 0, 0);
        _selected = _rows.Count == 0 ? -1 : Math.Clamp(_selected, 0, _rows.Count - 1);
        if (_selected >= 0 && _rows[_selected].Item.Disabled)
        {
            SkipDisabledForward();
        }
    }

    private void AddLevel(IReadOnlyList<MenuItem> items, int depth, int level)
    {
        for (int i = 0; i < items.Count; i++)
        {
            var item = items[i];
            bool expanded = item.Children is { Count: > 0 }
                && level < _expanded.Count
                && _expanded[level] == i;
            _rows.Add(new MenuRow(item, depth, expanded, i));
            if (expanded && depth < MaxDepth)
            {
                AddLevel(item.Children!, depth + 1, level + 1);
            }
        }
    }

    private void SkipDisabledForward()
    {
        for (int n = 0; n < _rows.Count; n++)
        {
            if (_selected >= 0 && _selected < _rows.Count && !_rows[_selected].Item.Disabled)
            {
                return;
            }

            _selected++;
            if (_selected >= _rows.Count)
            {
                _selected = 0;
            }
        }
    }

    private bool OpenSubmenu()
    {
        var row = Selected;
        if (row is null || row.Value.Item.Children is not { Count: > 0 } || row.Value.IsExpanded)
        {
            return false;
        }

        _expanded.Add(row.Value.Index);
        _returnTo.Add(_selected);
        Rebuild();
        _selected++;
        if (_selected >= _rows.Count)
        {
            _selected = _rows.Count - 1;
        }

        SkipDisabledForward();
        return true;
    }

    private bool CloseSubmenu()
    {
        if (_expanded.Count == 0)
        {
            return false;
        }

        _expanded.RemoveAt(_expanded.Count - 1);
        int restore = _returnTo[_returnTo.Count - 1];
        _returnTo.RemoveAt(_returnTo.Count - 1);
        Rebuild();
        _selected = Math.Clamp(restore, 0, _rows.Count - 1);
        return true;
    }

    private bool Activate()
    {
        var row = Selected;
        if (row is null)
        {
            return false;
        }

        if (row.Value.Item.Disabled)
        {
            return true;
        }

        if (row.Value.Item.Children is { Count: > 0 })
        {
            return OpenSubmenu();
        }

        _onActivate?.Invoke(row.Value.Item.Id);
        Close();
        return true;
    }

    private bool ActivateHotkey(Rune rune)
    {
        char key = char.ToUpperInvariant((char)rune.Value);
        for (int i = 0; i < _rows.Count; i++)
        {
            var row = _rows[i];
            string hotkey = row.Item.Hotkey;
            if (row.Item.Disabled || hotkey.Length == 0
                || char.ToUpperInvariant(hotkey[0]) != key)
            {
                continue;
            }

            _selected = i;
            if (row.Item.Children is { Count: > 0 })
            {
                return OpenSubmenu();
            }

            return Activate();
        }

        return false;
    }
}
