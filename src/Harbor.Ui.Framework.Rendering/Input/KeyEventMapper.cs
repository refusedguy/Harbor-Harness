using System.Text;
using Harbor.Ui.Framework.State;

namespace Harbor.Ui.Framework.Rendering.Input;

/// <summary>
///     Single translation point from decoded terminal keys (<see cref="KeyEvent" />)
///     to the framework key vocabulary (<see cref="UiKey" />). Every renderer maps
///     through here before emitting <see cref="UiMsg.KeyInput" />, so one physical
///     key means the same action in all renderers (epic C).
/// </summary>
public static class KeyEventMapper
{
    /// <summary>
    ///     Translate a key event, or null when it carries no key meaning
    ///     (releases, unmapped codes). Lossy corners are explicit:
    ///     <c>Insert</c>/<c>Delete</c>/<c>Unknown</c> have no
    ///     <see cref="UiKeyCode" /> counterpart; <c>Meta</c> folds into
    ///     <c>Alt</c> (terminal convention); non-BMP runes drop the character
    ///     (the reducer treats a char-less <c>Char</c> as noop).
    /// </summary>
    public static UiKey? ToUiKey(in KeyEvent evt)
    {
        if (evt.EventType == KeyEventType.Release)
        {
            return null;
        }

        UiKeyCode code = evt.Key switch
        {
            KeyCode.None => UiKeyCode.None,
            KeyCode.Char => UiKeyCode.Char,
            KeyCode.Enter => UiKeyCode.Enter,
            KeyCode.Tab => UiKeyCode.Tab,
            KeyCode.Backspace => UiKeyCode.Backspace,
            KeyCode.Escape => UiKeyCode.Escape,
            KeyCode.Up => UiKeyCode.Up,
            KeyCode.Down => UiKeyCode.Down,
            KeyCode.Left => UiKeyCode.Left,
            KeyCode.Right => UiKeyCode.Right,
            KeyCode.Home => UiKeyCode.Home,
            KeyCode.End => UiKeyCode.End,
            KeyCode.PageUp => UiKeyCode.PageUp,
            KeyCode.PageDown => UiKeyCode.PageDown,
            KeyCode.F1 => UiKeyCode.F1,
            KeyCode.F2 => UiKeyCode.F2,
            KeyCode.F3 => UiKeyCode.F3,
            KeyCode.F4 => UiKeyCode.F4,
            KeyCode.F5 => UiKeyCode.F5,
            KeyCode.F6 => UiKeyCode.F6,
            KeyCode.F7 => UiKeyCode.F7,
            KeyCode.F8 => UiKeyCode.F8,
            KeyCode.F9 => UiKeyCode.F9,
            KeyCode.F10 => UiKeyCode.F10,
            KeyCode.F11 => UiKeyCode.F11,
            KeyCode.F12 => UiKeyCode.F12,
            _ => UiKeyCode.None,
        };

        KeyModifierSet mods = KeyModifierSet.None;
        if (evt.Modifiers.HasFlag(KeyModifiers.Shift)) mods |= KeyModifierSet.Shift;
        if (evt.Modifiers.HasFlag(KeyModifiers.Ctrl)) mods |= KeyModifierSet.Ctrl;
        if (evt.Modifiers.HasFlag(KeyModifiers.Alt) || evt.Modifiers.HasFlag(KeyModifiers.Meta)) mods |= KeyModifierSet.Alt;

        char? ch = null;
        if (evt.Key == KeyCode.Char && evt.Character.Value <= 0xFFFF)
        {
            ch = (char)evt.Character.Value;
        }

        return new UiKey(code, mods, ch);
    }

    /// <summary>Translate from parts (for renderers with their own key type).</summary>
    public static UiKey FromParts(UiKeyCode code, KeyModifierSet mods = KeyModifierSet.None, char? ch = null) =>
        new(code, mods, code == UiKeyCode.Char ? ch : null);
}
