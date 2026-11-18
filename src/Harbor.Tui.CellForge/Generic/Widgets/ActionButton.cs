// #2370: focusable action row (Button) from gpui-kit-components.md. The action
// stays a message: the host supplies an Action callback and owns all app state;
// this type only announces activation. Focus rides the engine FocusRouter via
// IFocusTarget, and activation/hotkeys ride the existing KeyEvent pipeline with
// the command gate (#833). No new extension axis (#555), no dialog or plugin
// wiring. AOT-safe: no reflection, no capture beyond the host delegates.

using System.Text;

namespace Harbor.Tui.CellForge.Generic.Widgets;

/// <summary>
/// A declared accelerator for an <see cref="ActionButton"/>: a logical key,
/// the exact modifiers it needs, and the character when the key is
/// <see cref="KeyCode.Char"/>. Matching is exact (the same shape as
/// <c>KeyGesture.Matches</c>), so an undeclared Shift or chord is refused: the
/// command gate, not the text-buffer one.
/// </summary>
/// <param name="Key">Logical key identity.</param>
/// <param name="Modifiers">Exact modifiers required for a match.</param>
/// <param name="Character">Character carried when <paramref name="Key"/> is <see cref="KeyCode.Char"/>.</param>
public readonly record struct ActionHotkey(
    KeyCode Key,
    KeyModifiers Modifiers = KeyModifiers.None,
    char Character = '\0')
{
    /// <summary>A printable-character accelerator, e.g. 's'.</summary>
    public static ActionHotkey Char(char c, KeyModifiers mods = KeyModifiers.None) =>
        new(KeyCode.Char, mods, c);

    /// <summary>A named-key accelerator, e.g. <see cref="KeyCode.F2"/>.</summary>
    public static ActionHotkey Named(KeyCode key, KeyModifiers mods = KeyModifiers.None) =>
        new(key, mods);

    /// <summary>Whether <paramref name="key"/> is exactly this accelerator.</summary>
    public bool Matches(KeyEvent key) =>
        key.Key == Key
        && key.Modifiers == Modifiers
        && (Key != KeyCode.Char || key.Character.Value == Character);

    /// <summary>Short hint painted on the button, e.g. "S" or "^P".</summary>
    public string Display
    {
        get
        {
            string prefix = string.Empty;
            if ((Modifiers & KeyModifiers.Ctrl) != 0)
            {
                prefix += "^";
            }
            if ((Modifiers & KeyModifiers.Alt) != 0)
            {
                prefix += "Alt+";
            }
            if ((Modifiers & KeyModifiers.Shift) != 0)
            {
                prefix += "Shift+";
            }
            if ((Modifiers & KeyModifiers.Meta) != 0)
            {
                prefix += "Meta+";
            }

            string keyText = Key == KeyCode.Char && Character != '\0'
                ? char.ToUpperInvariant(Character).ToString()
                : Key.ToString();
            return prefix + keyText;
        }
    }
}

/// <summary>
/// A focusable action row with a caption, an optional glyph and an optional
/// hotkey slot. The host owns the enabled state through a getter and the action
/// through a callback; this type holds only focus state. Enter/Space activate
/// the focused row, and a declared accelerator activates it from anywhere in a
/// <see cref="Toolbar"/>. It implements the engine
/// <see cref="Harbor.Tui.CellForge.Input.IFocusTarget"/>, so a host adds it to a
/// <see cref="Harbor.Tui.CellForge.Input.FocusRouter"/> Tab order.
/// </summary>
public sealed class ActionButton : Harbor.Tui.CellForge.Input.IFocusTarget
{
    private readonly Action _onActivate;
    private readonly Func<bool> _isEnabled;

    /// <summary>
    /// Creates an action row. <paramref name="onActivate"/> is the action
    /// message the host handles; nothing here mutates app state directly.
    /// </summary>
    public ActionButton(
        string id,
        string label,
        Action onActivate,
        string glyph = "",
        ActionHotkey? hotkey = null,
        Func<bool>? isEnabled = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(onActivate);
        Id = id;
        Label = label ?? string.Empty;
        Glyph = glyph ?? string.Empty;
        Hotkey = hotkey;
        _onActivate = onActivate;
        _isEnabled = isEnabled ?? (static () => true);
    }

    /// <inheritdoc />
    public string Id { get; }

    /// <summary>Caption painted beside the glyph.</summary>
    public string Label { get; set; }

    /// <summary>Optional short glyph painted before the caption.</summary>
    public string Glyph { get; }

    /// <summary>Optional accelerator shown as a hint and matched on key input.</summary>
    public ActionHotkey? Hotkey { get; }

    /// <summary>Whether this row may own focus right now.</summary>
    public bool CanFocus => _isEnabled();

    /// <summary>Whether this row currently owns focus.</summary>
    public bool IsFocused { get; private set; }

    /// <summary>Raised when focus or activation changes so a host can repaint.</summary>
    public event EventHandler? Changed;

    /// <summary>FocusRouter callback: track focus so Paint can show the marker.</summary>
    public void OnFocusChanged(bool focused)
    {
        IsFocused = focused;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Whether <paramref name="key"/> is this row's declared accelerator.</summary>
    public bool MatchesHotkey(KeyEvent key) => Hotkey is { } hotkey && hotkey.Matches(key);

    /// <summary>
    /// Routes one key through the command gate. Enter or Space on the press
    /// (or repeat) phase, or an exact accelerator match, activates the row;
    /// chords, releases and unbound keys fall through. A disabled row refuses
    /// every key, so the callback can never fire for it.
    /// </summary>
    public bool HandleKey(KeyEvent key)
    {
        if (!CanFocus || key.EventType is not (KeyEventType.Press or KeyEventType.Repeat))
        {
            return false;
        }

        bool activate = key.Modifiers.IsUnmodified()
            && (key.Key == KeyCode.Enter
                || (key.Key == KeyCode.Char && key.Character.Value == ' '));

        if (!activate && Hotkey is { } hotkey)
        {
            activate = hotkey.Matches(key);
        }

        if (!activate)
        {
            return false;
        }

        _onActivate();
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>Width this row wants in a single-line toolbar.</summary>
    public int MeasureWidth()
    {
        int width = 2; // focus marker
        if (Glyph.Length > 0)
        {
            width += Glyph.Length + 1;
        }
        width += Label.Length;
        if (Hotkey is { } hotkey)
        {
            string hint = hotkey.Display;
            if (hint.Length > 0)
            {
                width += hint.Length + 3; // " [" + hint + "]"
            }
        }

        return width;
    }

    /// <summary>
    /// Paints the row into <paramref name="rect"/>: a focus marker, the glyph,
    /// the caption and the hotkey hint. Focus reverses the marker, a disabled
    /// row dims everything. Degenerate rects are a no-op.
    /// </summary>
    public void Paint(ScreenBuffer buffer, Rect rect)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        if (rect.Width <= 0 || rect.Height <= 0)
        {
            return;
        }

        var rowStyle = new CellStyle(ChatPalette.Text, attrs: CanFocus ? StyleAttr.None : StyleAttr.Dim);
        var markerStyle = new CellStyle(
            ChatPalette.Accent,
            attrs: CanFocus ? (IsFocused ? StyleAttr.Reverse : StyleAttr.Bold) : StyleAttr.Dim);

        var text = new StringBuilder(MeasureWidth());
        text.Append(IsFocused ? '>' : ' ').Append(' ');
        if (Glyph.Length > 0)
        {
            text.Append(Glyph).Append(' ');
        }
        text.Append(Label);
        if (Hotkey is { } hotkey)
        {
            string hint = hotkey.Display;
            if (hint.Length > 0)
            {
                text.Append(" [").Append(hint).Append(']');
            }
        }

        string row = text.ToString();
        buffer.SetText(rect.X, rect.Y, row.Length > rect.Width ? row[..rect.Width] : row, rowStyle);
        buffer.SetStyleAt(rect.X, rect.Y, markerStyle);
    }
}
