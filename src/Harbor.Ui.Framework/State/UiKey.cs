namespace Harbor.Ui.Framework.State;
/// <summary>
///     Abstract key code, free of any specific TUI framework so every renderer
///     (Spectre, Plain, Fullscreen, ANSI) maps its native key onto the same type.
/// </summary>
public enum UiKeyCode : byte
{
    None,
    Char,
    Up,
    Down,
    Left,
    Right,
    PageUp,
    PageDown,
    Home,
    End,
    Enter,
    Escape,
    Backspace,
    Tab,
    F1,
    F2,
    F3,
    F4,
    /// <summary>F5 — reserved for future use (reload config / replay).</summary>
    F5,
    /// <summary>F6 — reserved for future use.</summary>
    F6,
    /// <summary>F7 — reserved for future use.</summary>
    F7,
    /// <summary>F8 — reserved for future use.</summary>
    F8,
    /// <summary>F9 — reserved for future use.</summary>
    F9,
    /// <summary>F10 — reserved for future use.</summary>
    F10,
    /// <summary>F11 — reserved for future use.</summary>
    F11,
    /// <summary>F12 — toggles the in-TUI diagnostics / logs panel.</summary>
    F12
}

/// <summary>Modifier set for a <see cref="UiKey" /> (bitwise-combinable).</summary>
[Flags]
public enum KeyModifierSet : byte
{
    None = 0,
    Shift = 1,
    Ctrl = 2,
    Alt = 4
}

/// <summary>
///     Framework-neutral key press. Renderers translate their native key type into
///     this before emitting <see cref="UiMsg.KeyInput" />, so the reducer never
///     depends on a concrete TUI library.
/// </summary>
/// <param name="Code">The abstract key code.</param>
/// <param name="Mods">Active modifiers.</param>
/// <param name="Character">The character for <see cref="UiKeyCode.Char" />, else null.</param>
public readonly record struct UiKey(UiKeyCode Code, KeyModifierSet Mods = KeyModifierSet.None, char? Character = null)
{
    public static readonly UiKey Unknown = new(UiKeyCode.None);

    public bool Has(KeyModifierSet mod) => Mods.HasFlag(mod);

    public static UiKey ForChar(char c, KeyModifierSet mods = KeyModifierSet.None)
        => new(UiKeyCode.Char, mods, c);
}
