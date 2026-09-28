using System.Text;

namespace Harbor.Ui.Framework.Rendering.Input;

/// <summary>
///     Single translation point from decoded terminal keys (<see cref="KeyEvent" />)
///     to the BCL-only key vocabulary (<see cref="UiKeyDto" />). Every renderer maps
///     through here before the State-side <c>KeyEventAdapter</c> turns the DTO into
///     <c>UiKey</c> / <c>AppMsg.KeyInput</c>, so one physical key means the same
///     action in all renderers (epic C). Deliberately free of any
///     <c>Harbor.Ui.Framework.State</c> reference (epic #33 T1): this assembly is a
///     leaf over DesignSystem / Desktop.Animations / Abstractions.Contracts.
/// </summary>
public static class KeyEventMapper
{
    /// <summary>
    ///     Translate a key event, or null when it carries no key meaning
    ///     (releases, unmapped codes). Lossy corners are explicit:
    ///     <c>Insert</c>/<c>Delete</c>/<c>Unknown</c> have no
    ///     <see cref="UiKeyKind" /> counterpart; <c>Meta</c> folds into
    ///     <c>Alt</c> (terminal convention); non-BMP runes drop the character
    ///     (the reducer treats a char-less <c>Char</c> as noop).
    /// </summary>
    public static UiKeyDto? ToDto(in KeyEvent evt)
    {
        if (evt.EventType == KeyEventType.Release)
        {
            return null;
        }

        UiKeyKind kind = evt.Key switch
        {
            KeyCode.None => UiKeyKind.None,
            KeyCode.Char => UiKeyKind.Char,
            KeyCode.Enter => UiKeyKind.Enter,
            KeyCode.Tab => UiKeyKind.Tab,
            KeyCode.Backspace => UiKeyKind.Backspace,
            KeyCode.Escape => UiKeyKind.Escape,
            KeyCode.Up => UiKeyKind.Up,
            KeyCode.Down => UiKeyKind.Down,
            KeyCode.Left => UiKeyKind.Left,
            KeyCode.Right => UiKeyKind.Right,
            KeyCode.Home => UiKeyKind.Home,
            KeyCode.End => UiKeyKind.End,
            KeyCode.PageUp => UiKeyKind.PageUp,
            KeyCode.PageDown => UiKeyKind.PageDown,
            KeyCode.F1 => UiKeyKind.F1,
            KeyCode.F2 => UiKeyKind.F2,
            KeyCode.F3 => UiKeyKind.F3,
            KeyCode.F4 => UiKeyKind.F4,
            KeyCode.F5 => UiKeyKind.F5,
            KeyCode.F6 => UiKeyKind.F6,
            KeyCode.F7 => UiKeyKind.F7,
            KeyCode.F8 => UiKeyKind.F8,
            KeyCode.F9 => UiKeyKind.F9,
            KeyCode.F10 => UiKeyKind.F10,
            KeyCode.F11 => UiKeyKind.F11,
            KeyCode.F12 => UiKeyKind.F12,
            _ => UiKeyKind.None,
        };

        UiKeyMods mods = UiKeyMods.None;
        if (evt.Modifiers.HasFlag(KeyModifiers.Shift)) mods |= UiKeyMods.Shift;
        if (evt.Modifiers.HasFlag(KeyModifiers.Ctrl)) mods |= UiKeyMods.Ctrl;
        if (evt.Modifiers.HasFlag(KeyModifiers.Alt) || evt.Modifiers.HasFlag(KeyModifiers.Meta)) mods |= UiKeyMods.Alt;

        char? ch = null;
        if (evt.Key == KeyCode.Char && evt.Character.Value <= 0xFFFF)
        {
            ch = (char)evt.Character.Value;
        }

        return new UiKeyDto(kind, mods, ch);
    }

    /// <summary>
    ///     <see cref="IKeyVocabulary" /> entry point: translate a key event,
    ///     or return false when it carries no key meaning.
    /// </summary>
    public static bool TryMap(in KeyEvent evt, out UiKeyDto dto)
    {
        var mapped = ToDto(evt);
        if (mapped is null)
        {
            dto = default;
            return false;
        }

        dto = mapped.Value;
        return true;
    }

    /// <summary>Translate from parts (for renderers with their own key type).</summary>
    public static UiKeyDto FromParts(UiKeyKind kind, UiKeyMods mods = UiKeyMods.None, char? ch = null) =>
        new(kind, mods, kind == UiKeyKind.Char ? ch : null);
}
