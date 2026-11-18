namespace Harbor.Ui.Framework.Rendering.Input;

/// <summary>
/// Logical key identity. <see cref="KeyCode.Char"/> keys carry the actual
/// character in <see cref="KeyEvent.Character"/>; every other member is a
/// named function key.
/// </summary>
public enum KeyCode : byte
{
    None = 0,

    /// <summary>Printable character input (<see cref="KeyEvent.Character"/> is set).</summary>
    Char,

    Enter,
    Tab,
    Backspace,
    Escape,

    Up,
    Down,
    Left,
    Right,

    Home,
    End,
    PageUp,
    PageDown,
    Insert,
    Delete,

    F1,
    F2,
    F3,
    F4,
    F5,
    F6,
    F7,
    F8,
    F9,
    F10,
    F11,
    F12,

    // Numpad keys keep their physical identity so games (NetHack/DCSS style
    // numpad movement) and shortcuts do not have to guess from the character.
    // Kitty CSI-u codes 57399..57408, 57409, 57412, 57413, 57414. Legacy
    // terminals only distinguish the operators/Enter in application keypad
    // mode (SS3 k/m/M); digits and decimal collide with arrows/Delete there,
    // so the kitty disambiguate flag is required to name those reliably.
    /// <summary>Numeral keypad digit keys, zero through nine.</summary>
    Keypad0,
    Keypad1,
    Keypad2,
    Keypad3,
    Keypad4,
    Keypad5,
    Keypad6,
    Keypad7,
    Keypad8,
    Keypad9,

    /// <summary>Keypad Enter (kitty KP_ENTER / legacy SS3 M).</summary>
    KeypadEnter,

    /// <summary>Keypad + (kitty KP_ADD / legacy SS3 k).</summary>
    KeypadPlus,

    /// <summary>Keypad - (kitty KP_SUBTRACT / legacy SS3 m).</summary>
    KeypadMinus,

    /// <summary>Keypad decimal point (kitty KP_DECIMAL; ambiguous with Delete in legacy mode).</summary>
    KeypadDecimal,

    /// <summary>A key the decoder could not name. Raw codepoint is preserved
    /// in <see cref="KeyEvent.Codepoint"/> when known (forward compatibility
    /// with unmappped kitty functional codes).</summary>
    Unknown,
}
