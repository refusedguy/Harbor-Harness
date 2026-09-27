namespace Harbor.Ui.Framework.Rendering.Input;

/// <summary>
///     Renderer-side key kind. BCL-only mirror of the State-side
///     <c>UiKeyCode</c> (epic #33 T1): same members, same order, same explicit
///     values — the State-side <c>KeyEventAdapter</c> maps this onto
///     <c>UiKeyCode</c> explicitly, so the two enums must stay 1:1.
/// </summary>
public enum UiKeyKind : byte
{
    None = 0,
    Char = 1,
    Up = 2,
    Down = 3,
    Left = 4,
    Right = 5,
    PageUp = 6,
    PageDown = 7,
    Home = 8,
    End = 9,
    Enter = 10,
    Escape = 11,
    Backspace = 12,
    Tab = 13,
    F1 = 14,
    F2 = 15,
    F3 = 16,
    F4 = 17,
    F5 = 18,
    F6 = 19,
    F7 = 20,
    F8 = 21,
    F9 = 22,
    F10 = 23,
    F11 = 24,
    F12 = 25,
}

/// <summary>
///     Renderer-side modifier set. BCL-only mirror of the State-side
///     <c>KeyModifierSet</c> (epic #33 T1): same flags, same values.
/// </summary>
[Flags]
public enum UiKeyMods : byte
{
    None = 0,
    Shift = 1,
    Ctrl = 2,
    Alt = 4,
}

/// <summary>
///     Framework-neutral key press, free of any State reference. Renderers
///     produce this via <see cref="KeyEventMapper" />; the State-side
///     <c>KeyEventAdapter</c> converts it into <c>UiKey</c> /
///     <c>UiMsg.KeyInput</c>. BCL-only and AOT-compatible: no reflection,
///     no allocations beyond the nullable wrapper.
/// </summary>
/// <param name="Kind">The abstract key kind.</param>
/// <param name="Mods">Active modifiers.</param>
/// <param name="Character">The character for <see cref="UiKeyKind.Char" />, else null.</param>
public readonly record struct UiKeyDto(UiKeyKind Kind, UiKeyMods Mods = UiKeyMods.None, char? Character = null)
{
    public static readonly UiKeyDto Unknown = new(UiKeyKind.None);

    public bool Has(UiKeyMods mod) => Mods.HasFlag(mod);

    public static UiKeyDto ForChar(char c, UiKeyMods mods = UiKeyMods.None)
        => new(UiKeyKind.Char, mods, c);
}

/// <summary>
///     Key-vocabulary contract: decode a <see cref="KeyEvent" /> into the
///     BCL-only <see cref="UiKeyDto" />. The default implementation is
///     <see cref="DefaultKeyVocabulary" /> (delegates to
///     <see cref="KeyEventMapper" />); hosts may substitute their own for
///     tests without touching the shared mapping table.
/// </summary>
public interface IKeyVocabulary
{
    /// <summary>
    ///     Translate a key event, or return false when it carries no key
    ///     meaning (releases, unmapped codes).
    /// </summary>
    bool TryMap(in KeyEvent evt, out UiKeyDto key);
}

/// <summary>Default <see cref="IKeyVocabulary" /> over the shared mapping table.</summary>
public sealed class DefaultKeyVocabulary : IKeyVocabulary
{
    /// <inheritdoc />
    public bool TryMap(in KeyEvent evt, out UiKeyDto key) =>
        KeyEventMapper.TryMap(evt, out key);
}
