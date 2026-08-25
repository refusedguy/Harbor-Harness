using System.Text;

namespace Harbor.Tui.ConsoleEx.Input;

/// <summary>
/// A decoded keyboard event. <see cref="Character"/> is meaningful only when
/// <see cref="KeyCode.Char"/> == <see cref="Key"/>; <see cref="Codepoint"/>
/// preserves the raw unicode-key-code for <see cref="KeyCode.Unknown"/> keys.
/// </summary>
/// <param name="key">Named logical key.</param>
/// <param name="character">Decoded character for <see cref="Input.KeyCode.Char"/> keys.</param>
/// <param name="modifiers">Shift/Ctrl/Alt/Meta set active during the event.</param>
/// <param name="eventType">Press/repeat/release phase.</param>
/// <param name="isKittyEncoded">
/// True when the event came from the kitty keyboard protocol. False means the
/// legacy encoder produced it — ambiguous combinations (e.g. Shift+Enter,
/// Ctrl+I vs Tab) were NOT distinguishable and degraded explicitly, never silently.
/// </param>
/// <param name="codepoint">Raw unicode-key-code when <see cref="Input.KeyCode.Unknown"/>.</param>
public readonly struct KeyEvent(
    KeyCode key,
    Rune character,
    KeyModifiers modifiers,
    KeyEventType eventType,
    bool isKittyEncoded,
    uint codepoint = 0)
{
    public KeyCode Key { get; } = key;
    public Rune Character { get; } = character;
    public KeyModifiers Modifiers { get; } = modifiers;
    public KeyEventType EventType { get; } = eventType;
    public bool IsKittyEncoded { get; } = isKittyEncoded;
    public uint Codepoint { get; } = codepoint;

    public static KeyEvent Char(Rune character, KeyModifiers modifiers = KeyModifiers.None, bool isKittyEncoded = false) =>
        new(KeyCode.Char, character, modifiers, KeyEventType.Press, isKittyEncoded);

    public static KeyEvent Simple(KeyCode key, KeyModifiers modifiers = KeyModifiers.None, bool isKittyEncoded = false) =>
        new(key, default, modifiers, KeyEventType.Press, isKittyEncoded);

    public override string ToString() =>
        $"{EventType} {Key} {(Key == KeyCode.Char ? $"'{Character}'" : string.Empty)} [{Modifiers}]{(IsKittyEncoded ? " kitty" : " legacy")}";
}
