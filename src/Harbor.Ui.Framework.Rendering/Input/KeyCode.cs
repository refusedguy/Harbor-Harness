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

    /// <summary>A key the decoder could not name. Raw codepoint is preserved
    /// in <see cref="KeyEvent.Codepoint"/> when known (forward compatibility
    /// with unmappped kitty functional codes).</summary>
    Unknown,
}
