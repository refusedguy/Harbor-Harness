// Issue #2377: DropdownButton is the composition the research catalog asks
// for: a button with a primary action plus an anchored menu reached from the
// caret. It owns neither action semantics nor menu items: Enter/Space calls
// the host's action, Down opens the menu, and a menu selection calls the
// host's on-select with the item id. No fuzzy search or history (#2321), and
// no new extension axis.

namespace Harbor.Tui.CellForge.Generic.Widgets;

/// <summary>
/// A button with an anchored menu. The host places the button rect and the
/// viewport; <see cref="Bounds"/> is where the popup will paint. While open,
/// overlay focus keys are tried first, then menu keys, so Tab never escapes
/// the popup and the host's screen behind it stays untouched.
/// </summary>
public sealed class DropdownButton : IFocusTarget
{
    /// <summary>Caret drawn after the label to signal the menu half.</summary>
    public const char Caret = '\u25be';

    private static readonly CellStyle ButtonStyle = new(attrs: StyleAttr.Bold);

    private readonly AnchoredOverlay _overlay;
    private readonly AnchoredMenu _menu;
    private readonly Action _onAction;
    private string _label;

    /// <summary>Creates a dropdown over a primary action and a menu catalog.</summary>
    public DropdownButton(
        string id,
        string label,
        Action onAction,
        IEnumerable<MenuItem> items,
        Action<string>? onSelect = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(label);
        ArgumentNullException.ThrowIfNull(onAction);

        Id = id;
        _label = label;
        _onAction = onAction;
        _menu = new AnchoredMenu(items, itemId =>
        {
            Close();
            onSelect?.Invoke(itemId);
        });
        _overlay = new AnchoredOverlay(new AnchorCell(0, 0), _menu.ContentSize, new Rect(0, 0, 1, 1));
    }

    /// <inheritdoc />
    public string Id { get; }

    /// <summary>True while the host's focus router has this button.</summary>
    public bool Focused { get; private set; }

    /// <summary>Visible button text.</summary>
    public string Label => _label;

    /// <summary>The anchored menu, for hosts that need the selection.</summary>
    public AnchoredMenu Menu => _menu;

    /// <summary>Resolved popup rect.</summary>
    public Rect Bounds => _overlay.Bounds;

    /// <summary>Whether the menu half is showing.</summary>
    public bool IsOpen => _overlay.IsOpen;

    /// <summary>Rect the button occupies; set by <see cref="Place"/>.</summary>
    public Rect ButtonRect { get; private set; }

    /// <summary>Replaces the visible label.</summary>
    public void SetLabel(string label)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(label);
        _label = label;
    }

    /// <summary>Sets the button rect and anchors the popup to its bottom-left cell.</summary>
    public void Place(Rect buttonRect, Rect viewport, AnchorSide side = AnchorSide.Bottom, int gap = 0)
    {
        ButtonRect = buttonRect;
        _overlay.Place(
            new AnchorCell(buttonRect.X, buttonRect.Bottom - 1),
            _menu.ContentSize,
            viewport,
            side,
            gap);
    }

    /// <inheritdoc />
    public void OnFocusChanged(bool focused) => Focused = focused;

    /// <summary>Opens the menu half and focuses its first slot.</summary>
    public void Open()
    {
        _menu.Open();
        _overlay.Place(
            new AnchorCell(ButtonRect.X, ButtonRect.Bottom - 1),
            _menu.ContentSize,
            _overlay.Viewport);
        _overlay.SetFocusCount(_menu.Rows.Count);
        _overlay.FocusSlot(Math.Max(0, _menu.SelectedIndex));
        _overlay.Open();
    }

    /// <summary>Closes the menu half.</summary>
    public void Close()
    {
        _overlay.Close();
        _menu.Close();
    }

    /// <summary>
    /// Routes one key. When closed, Enter/Space runs the action and Down opens
    /// the menu. When open, the focus trap gets the key first, then the menu.
    /// Press and repeat only, with a strict command gate: Shift and chords are
    /// never consumed here.
    /// </summary>
    public bool HandleKey(in KeyEvent key)
    {
        if (key.EventType != KeyEventType.Press && key.EventType != KeyEventType.Repeat
            || !key.Modifiers.IsUnmodified())
        {
            return false;
        }

        if (!IsOpen)
        {
            if (key.Key == KeyCode.Enter || (key.Key == KeyCode.Char && key.Character.Value == ' '))
            {
                _onAction();
                return true;
            }

            if (key.Key == KeyCode.Down)
            {
                Open();
                return true;
            }

            return false;
        }

        if (_overlay.HandleKey(key))
        {
            if (!_overlay.IsOpen)
            {
                Close();
            }

            return true;
        }

        bool handled = _menu.HandleKey(key);
        if (handled)
        {
            _overlay.FocusSlot(Math.Max(0, _menu.SelectedIndex));
        }

        return handled;
    }

    /// <summary>Paints the button and, when open, the menu in its anchored bounds.</summary>
    public void Paint(ScreenBuffer buffer)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        if (ButtonRect.Width > 0 && ButtonRect.Height > 0)
        {
            buffer.Fill(ButtonRect, Cell.Blank);
            string text = "[ " + _label + " " + Caret + " ]";
            buffer.SetText(ButtonRect.X, ButtonRect.Y, text.AsSpan(0, Math.Min(text.Length, ButtonRect.Width)),
                Focused ? ButtonStyle : CellStyle.Plain);
        }

        if (IsOpen)
        {
            _menu.Paint(buffer, Bounds);
        }
    }
}
