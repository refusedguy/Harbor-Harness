// #2365: one controlled-value primitive for the "switches and choice" family
// (Checkbox, Switch, Radio, Toggle, Select) from gpui-kit-components.md.
//
// The contract is deliberately small: the value lives OUTSIDE the widget (a
// Func<int> getter) and the widget only ever announces the NEXT value through
// an Action<int>. For the boolean styles (Checkbox/Switch/Toggle) the value is
// 0 = off / 1 = on; for the single-select styles (Radio/Select) it is the index
// of the chosen option. One value type, one change notification, five glyph
// sets. No new extension axis, no dialog or plugin wiring.
//
// Focus and activation ride the existing input stack: the type implements the
// engine IFocusTarget, so a host adds it to FocusRouter and the router's Tab
// order drives focus; HandleKey is the bubble gesture the focused entry gets.
// The DialogOverlay radio/select widgets are left untouched. This is a
// neutral primitive, not a rewrite of the dialog.

namespace Harbor.Tui.CellForge.Generic.Widgets;

/// <summary>
/// Presentation variant of <see cref="GenericToggleSelect"/>. The first three
/// are boolean toggles (value 0/1); <see cref="Radio"/> and <see cref="Select"/>
/// are single-choice lists (value = option index).
/// </summary>
public enum ToggleSelectStyle
{
    Checkbox,
    Switch,
    Radio,
    Toggle,
    Select,
}

/// <summary>
/// Host-agnostic controlled-value toggle/select primitive. The host owns the
/// value and supplies a getter plus an <c>on_change(next)</c> callback; this
/// type holds only ephemeral navigation state (the list cursor) and paints the
/// current value. It is a <see cref="Harbor.Tui.CellForge.Input.IFocusTarget"/>,
/// so it joins a <see cref="Harbor.Tui.CellForge.Input.FocusRouter"/> Tab order
/// and receives keys through its HandleKey; activation (Enter or Space) is the
/// only gesture that changes a value.
/// </summary>
/// <remarks>
/// The five gpui-kit components collapse to two behaviours: a boolean flip and
/// a one-of-N choice. Both use the same <see cref="Value"/> channel (an int),
/// the same <see cref="SelectedIndex"/>/<see cref="Checked"/> reads, the same <see cref="Disabled"/>
/// guard and the same activation keys. Arrow keys only move the cursor for the
/// list styles; they never change the value. That stays with the host's
/// on_change, so a controlled host is never bypassed.
/// </remarks>
public sealed class GenericToggleSelect : Harbor.Tui.CellForge.Input.IFocusTarget
{
    private readonly List<string> _options = new();
    private readonly Func<int> _getValue;
    private readonly Action<int> _onChange;
    private int _cursor;
    private bool _disabled;

    /// <summary>
    /// Creates a toggle/select with a stable focus id and the controlled-value
    /// channel. <paramref name="getValue"/> reads the current value (0/1 for
    /// boolean styles, option index for list styles);
    /// <paramref name="onChange"/> is the <c>on_change(next)</c> notification.
    /// </summary>
    public GenericToggleSelect(
        string id,
        ToggleSelectStyle style,
        Func<int> getValue,
        Action<int> onChange)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(getValue);
        ArgumentNullException.ThrowIfNull(onChange);
        Id = id;
        Style = style;
        _getValue = getValue;
        _onChange = onChange;
        Label = string.Empty;
    }

    /// <inheritdoc />
    public string Id { get; }

    /// <summary>The presentation variant.</summary>
    public ToggleSelectStyle Style { get; }

    /// <summary>True for Checkbox/Switch/Toggle (value 0/1).</summary>
    public bool IsBoolean => Style is ToggleSelectStyle.Checkbox
        or ToggleSelectStyle.Switch
        or ToggleSelectStyle.Toggle;

    /// <summary>Label painted beside a boolean toggle.</summary>
    public string Label { get; set; }

    /// <summary>Options for a single-select style (defensive copy).</summary>
    public IReadOnlyList<string> Options => _options;

    /// <summary>
    /// Whether the control is disabled. A disabled control reports
    /// <see cref="CanFocus"/> = false (hosts that honour it skip the seat) and
    /// refuses every key, so on_change can never fire for it.
    /// </summary>
    public bool Disabled
    {
        get => _disabled;
        set
        {
            if (_disabled == value)
            {
                return;
            }
            _disabled = value;
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Whether this entry may own focus right now.</summary>
    public bool CanFocus => !_disabled;

    /// <summary>Whether this entry currently owns focus.</summary>
    public bool IsFocused { get; private set; }

    /// <summary>Current controlled value, read straight from the host.</summary>
    public int Value => _getValue();

    /// <summary>Boolean read: true when the value is 1. False for list styles.</summary>
    public bool Checked => IsBoolean && Value == 1;

    /// <summary>
    /// Single-select read: the chosen option index, or -1 when the value is
    /// outside the option range. Always -1 for boolean styles.
    /// </summary>
    public int SelectedIndex
    {
        get
        {
            if (IsBoolean || _options.Count == 0)
            {
                return -1;
            }
            int value = Value;
            return value >= 0 && value < _options.Count ? value : -1;
        }
    }

    /// <summary>Cursor into the option list (list styles only); clamped.</summary>
    public int Cursor => _options.Count == 0 ? 0 : Math.Clamp(_cursor, 0, _options.Count - 1);

    /// <summary>Raised when focus, value or enabled state changes so a host can repaint.</summary>
    public event EventHandler? Changed;

    /// <summary>FocusRouter callback: track focus so Paint can show the focused marker.</summary>
    public void OnFocusChanged(bool focused)
    {
        IsFocused = focused;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Replaces the single-select options (copied; null entries coerce to
    /// empty) and seeds the cursor from the current controlled value.
    /// </summary>
    public void SetOptions(IReadOnlyList<string> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options.Clear();
        for (int i = 0; i < options.Count; i++)
        {
            _options.Add(options[i] ?? string.Empty);
        }
        int value = Value;
        _cursor = _options.Count == 0 ? 0 : Math.Clamp(value, 0, _options.Count - 1);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Routes one key through the command gate (press/repeat, no modifiers).
    /// Arrows move the cursor on list styles; Enter or Space activates every
    /// style and is the only path to <c>on_change(next)</c>. Returns true when
    /// consumed. A disabled control and a chord modifier are refused.
    /// </summary>
    public bool HandleKey(KeyEvent key)
    {
        if (_disabled
            || key.EventType is not (KeyEventType.Press or KeyEventType.Repeat)
            || !key.Modifiers.IsUnmodified())
        {
            return false;
        }

        bool space = key.Key == KeyCode.Char && key.Character.Value == ' ';

        if (!IsBoolean)
        {
            switch (key.Key)
            {
                case KeyCode.Up:
                case KeyCode.Left:
                    return Move(-1);
                case KeyCode.Down:
                case KeyCode.Right:
                    return Move(1);
                case KeyCode.Home:
                    return MoveTo(0);
                case KeyCode.End:
                    return MoveTo(_options.Count - 1);
            }
        }

        if (key.Key == KeyCode.Enter || space)
        {
            Activate();
            return true;
        }

        return false;
    }

    private bool Move(int delta)
    {
        if (_options.Count == 0 || delta == 0)
        {
            return false;
        }
        _cursor = Math.Clamp(_cursor + delta, 0, _options.Count - 1);
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    private bool MoveTo(int index)
    {
        if (_options.Count == 0)
        {
            return false;
        }
        _cursor = Math.Clamp(index, 0, _options.Count - 1);
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    private void Activate()
    {
        int next = IsBoolean ? (Value == 1 ? 0 : 1) : Cursor;
        _onChange(next);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Paints the control into <paramref name="rect"/>. A boolean style draws a
    /// single glyph plus its label; a list style draws one row per option with
    /// a cursor marker. Focus reverses the marker, disabled dims every row.
    /// </summary>
    public void Paint(ScreenBuffer buffer, Rect rect)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        if (rect.Width <= 0 || rect.Height <= 0)
        {
            return;
        }

        var textStyle = new CellStyle(ChatPalette.Text, attrs: _disabled ? StyleAttr.Dim : StyleAttr.None);
        var markerStyle = new CellStyle(
            ChatPalette.Accent,
            attrs: _disabled ? StyleAttr.Dim : (IsFocused ? StyleAttr.Reverse : StyleAttr.Bold));
        var cursorStyle = new CellStyle(ChatPalette.Accent, attrs: _disabled ? StyleAttr.Dim : StyleAttr.Bold);

        if (IsBoolean)
        {
            string glyph = Glyph(Value == 1);
            string text = glyph + (Label.Length == 0 ? string.Empty : " " + Label);
            Write(buffer, rect.X, rect.Y, text, rect.Width, textStyle);
            buffer.SetStyleAt(rect.X, rect.Y, markerStyle);
            return;
        }

        int rows = Math.Min(_options.Count, rect.Height);
        int selected = SelectedIndex;
        for (int r = 0; r < rows; r++)
        {
            bool isCursor = r == Cursor;
            bool isSelected = r == selected;
            string prefix = isCursor ? "> " : "  ";
            string text = prefix + Glyph(isSelected) + " " + _options[r];
            Write(buffer, rect.X, rect.Y + r, text, rect.Width, textStyle);
            if (isCursor)
            {
                buffer.SetStyleAt(rect.X, rect.Y + r, markerStyle);
            }
            if (isSelected)
            {
                buffer.SetStyleAt(rect.X + 2, rect.Y + r, cursorStyle);
            }
        }
    }

    private static void Write(ScreenBuffer buffer, int x, int y, string text, int width, in CellStyle style)
    {
        if (width <= 0)
        {
            return;
        }
        string clipped = text.Length > width ? text[..width] : text;
        buffer.SetText(x, y, clipped, style);
    }

    private string Glyph(bool on) => Style switch
    {
        ToggleSelectStyle.Checkbox => on ? "[x]" : "[ ]",
        ToggleSelectStyle.Switch => on ? "[=]" : "[-]",
        ToggleSelectStyle.Radio => on ? "(*)" : "( )",
        ToggleSelectStyle.Toggle => on ? "<x>" : "< >",
        ToggleSelectStyle.Select => on ? "[x]" : "[ ]",
        _ => on ? "[x]" : "[ ]",
    };
}
