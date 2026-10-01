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
/// The <see cref="KeyModifierSet" /> half of the command-modifier rule (#833).
/// </summary>
/// <remarks>
/// <para>
/// A sibling of <c>KeyModifierGate.AcceptsTypedChar</c> in
/// <c>Harbor.Ui.Framework.Rendering</c>, and deliberately not a redefinition of
/// it. There are THREE modifier vocabularies in the tree and they are not
/// interchangeable: <c>KeyModifiers</c> (kitty, with <c>Meta</c>),
/// <c>KeyModifierSet</c> (State, no <c>Meta</c> — <c>KeyEventMapper</c> folds
/// <c>Meta</c> into <c>Alt</c> on the way across) and
/// <see cref="System.ConsoleModifiers" /> (the BCL legacy path, also no
/// <c>Meta</c>). A shared helper would have to invent a <c>Meta</c> the State
/// vocabulary cannot express, and the result would be a mask that is wrong in
/// one of the three.
/// </para>
/// <para>
/// What IS shared is the rule: Ctrl and Alt are commands, Shift is a
/// case-shaper. So the rule is named once per vocabulary, in the assembly that
/// owns that vocabulary, and the three agree by construction rather than by
/// three copies staying in sync.
/// </para>
/// </remarks>
public static class KeyModifierSetGate
{
    /// <summary>The command modifiers, as one mask. Shift is deliberately absent.</summary>
    public const KeyModifierSet CommandMask = KeyModifierSet.Ctrl | KeyModifierSet.Alt;

    /// <summary>
    /// Whether NO modifier at all is held — the other half of the rule, for a
    /// panel whose runes are commands rather than text. Strictly narrower than
    /// <c>!IsCommandModifier()</c>, which still admits Shift.
    /// </summary>
    /// <param name="mods">The modifiers reported with the key press.</param>
    public static bool IsUnmodified(this KeyModifierSet mods) => mods == KeyModifierSet.None;

    /// <summary>
    /// Whether this key press may be treated as TEXT rather than as a chord.
    /// True when no command modifier is held — Shift alone still passes, because
    /// a capital arrives as the capital rune plus Shift.
    /// </summary>
    /// <param name="mods">The modifiers reported with the key press.</param>
    public static bool AcceptsTypedChar(this KeyModifierSet mods) => (mods & CommandMask) == 0;

    /// <summary>
    /// Whether a command modifier is held, i.e. the press is a chord the host
    /// owns and this panel must not claim.
    /// </summary>
    /// <param name="mods">The modifiers reported with the key press.</param>
    public static bool IsCommandModifier(this KeyModifierSet mods) => (mods & CommandMask) != 0;
}

/// <summary>
///     Framework-neutral key press. Renderers translate their native key type into
///     this before emitting <see cref="AppMsg.KeyInput" />, so the reducer never
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
