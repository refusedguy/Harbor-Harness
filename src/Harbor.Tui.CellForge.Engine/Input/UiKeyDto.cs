// #436: engine-owned port of the UiKeyDto half of
// src/Harbor.Ui.Framework.Rendering/Input/IKeyVocabulary.cs — verbatim except its namespace.
// The engine is a standalone leaf (zero Harbor references); this vocabulary lives here now.
// NOT ported: IKeyVocabulary/DefaultKeyVocabulary — they delegate to KeyEventMapper,
// which stays renderer-side. The engine produces UiKeyDto values (MouseRouter.WheelToKey);
// the host maps them into store messages through KeyEventAdapter, the single
// Rendering→State crossing point (#33/T1), unchanged.
namespace Harbor.Tui.CellForge.Input;

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
///     produce this; the State-side <c>KeyEventAdapter</c> converts it into
///     <c>UiKey</c> / <c>AppMsg.KeyInput</c>. BCL-only and AOT-compatible: no
///     reflection, no allocations beyond the nullable wrapper.
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
