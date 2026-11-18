// #2370: Toolbar container for the focusable action rows (Button) from
// gpui-kit-components.md. It owns a flat FocusRouter over its buttons, routes
// Tab/Shift+Tab to focus moves, fans accelerators out to any row, and paints
// the buttons as one line. The action stays a message: the container only
// forwards keys and focus, never decides what an activation means. No new
// extension axis (#555), no dialog or plugin wiring.

namespace Harbor.Tui.CellForge.Generic.Widgets;

/// <summary>
/// A single-line container of <see cref="ActionButton"/> rows. Buttons join a
/// flat <see cref="Harbor.Tui.CellForge.Input.FocusRouter"/> Tab order on add;
/// Tab and Shift+Tab move focus, accelerators fire on the row that declares
/// them, and Enter/Space go to the focused row. The host owns the buttons and
/// reads their callbacks; this type holds only the order.
/// </summary>
public sealed class Toolbar
{
    private readonly List<ActionButton> _buttons = [];
    private readonly Harbor.Tui.CellForge.Input.FocusRouter _focus = new();

    /// <summary>Creates a toolbar over the given rows, in Tab order.</summary>
    public Toolbar(params ActionButton[] buttons)
    {
        ArgumentNullException.ThrowIfNull(buttons);
        foreach (ActionButton button in buttons)
        {
            Add(button);
        }
    }

    /// <summary>The buttons in Tab order.</summary>
    public IReadOnlyList<ActionButton> Buttons => _buttons;

    /// <summary>The focus router this toolbar drives.</summary>
    public Harbor.Tui.CellForge.Input.FocusRouter FocusRouter => _focus;

    /// <summary>The focused row, when any.</summary>
    public ActionButton? Focused => _focus.Current as ActionButton;

    /// <summary>Adds a row at the end of the Tab order.</summary>
    public void Add(ActionButton button)
    {
        ArgumentNullException.ThrowIfNull(button);
        _buttons.Add(button);
        _focus.Add(button);
    }

    /// <summary>Focuses the next row, wrapping around.</summary>
    public bool FocusNext() => _focus.Next();

    /// <summary>Focuses the previous row, wrapping around.</summary>
    public bool FocusPrevious() => _focus.Previous();

    /// <summary>Focuses the row with the given id.</summary>
    public bool Focus(string id) => _focus.FocusById(id);

    /// <summary>Focuses the row at the given index.</summary>
    public bool FocusIndex(int index) => _focus.Jump(index);

    /// <summary>
    /// Routes one key. Tab and Shift+Tab move focus; a declared accelerator
    /// focuses and activates its row even when another row owns focus; any
    /// other key goes to the focused row. No rune is judged here, so this is a
    /// pass-through, not a key-gate site.
    /// </summary>
    public bool HandleKey(KeyEvent key)
    {
        if (key.EventType is not (KeyEventType.Press or KeyEventType.Repeat))
        {
            return false;
        }

        if (key.Key == KeyCode.Tab)
        {
            if (key.Modifiers == KeyModifiers.None)
            {
                return FocusNext();
            }
            if (key.Modifiers == KeyModifiers.Shift)
            {
                return FocusPrevious();
            }
        }

        ActionButton? focused = Focused;
        foreach (ActionButton button in _buttons)
        {
            // The focused row is handled below, so its accelerator is not
            // claimed twice.
            if (button.CanFocus && !ReferenceEquals(button, focused) && button.MatchesHotkey(key))
            {
                _focus.FocusById(button.Id);
                return button.HandleKey(key);
            }
        }

        return focused?.HandleKey(key) ?? false;
    }

    /// <summary>
    /// Paints the rows left to right with a one-cell separator. Each row draws
    /// its own focus marker from the shared FocusRouter state.
    /// </summary>
    public void Paint(ScreenBuffer buffer, Rect rect)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        if (rect.Width <= 0 || rect.Height <= 0)
        {
            return;
        }

        int x = rect.X;
        for (int i = 0; i < _buttons.Count && x < rect.X + rect.Width; i++)
        {
            int width = _buttons[i].MeasureWidth();
            int available = rect.X + rect.Width - x;
            _buttons[i].Paint(buffer, new Rect(x, rect.Y, Math.Min(width, available), rect.Height));
            x += width + 1;
        }
    }
}
