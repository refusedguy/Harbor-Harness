using Harbor.Ui.Framework.Rendering.Input;

namespace Harbor.Ui.Framework.State;

/// <summary>
///     Thin DTO→State bridge (epic #33 T1): converts the BCL-only
///     <see cref="UiKeyDto" /> produced by <see cref="KeyEventMapper" /> into
///     <see cref="UiKey" /> / <see cref="AppMsg.KeyInput" />. The mapping is
///     explicit and 1:1 with the mapper table — no behavior change, no
///     renderer-side branches. This is the ONLY place where the Rendering key
///     vocabulary crosses into State.
/// </summary>
public static class KeyEventAdapter
{
    /// <summary>Convert a mapped DTO into the State key type.</summary>
    public static UiKey ToUiKey(UiKeyDto dto) =>
        new(ToCode(dto.Kind), ToMods(dto.Mods), dto.Character);

    /// <summary>
    ///     Resolve a mapped DTO through <paramref name="keyMap" /> into the
    ///     store message.
    /// </summary>
    public static AppMsg.KeyInput ToKeyInput(UiKeyDto dto, ChatKeyMap keyMap)
    {
        ArgumentNullException.ThrowIfNull(keyMap);
        var key = ToUiKey(dto);
        return new AppMsg.KeyInput(keyMap.Resolve(key), key);
    }

    /// <summary>
    ///     Nullable overload: a null DTO (release / unmapped code) becomes a
    ///     <see cref="ChatAction.None" /> no-op the reducer drops.
    /// </summary>
    public static AppMsg.KeyInput ToKeyInput(UiKeyDto? dto, ChatKeyMap keyMap) =>
        dto is { } mapped ? ToKeyInput(mapped, keyMap) : new AppMsg.KeyInput(ChatAction.None, UiKey.Unknown);

    /// <summary>
    ///     Full path for hosts holding a raw <see cref="KeyEvent" />: map via
    ///     <see cref="KeyEventMapper" />, resolve via <paramref name="keyMap" />.
    ///     Returns false (with a <see cref="ChatAction.None" /> message) when the
    ///     event carries no key meaning.
    /// </summary>
    public static bool TryConvert(in KeyEvent evt, ChatKeyMap keyMap, out AppMsg.KeyInput msg)
    {
        ArgumentNullException.ThrowIfNull(keyMap);
        if (!KeyEventMapper.TryMap(evt, out var dto))
        {
            msg = new AppMsg.KeyInput(ChatAction.None, UiKey.Unknown);
            return false;
        }

        msg = ToKeyInput(dto, keyMap);
        return true;
    }

    private static UiKeyCode ToCode(UiKeyKind kind) => kind switch
    {
        UiKeyKind.None => UiKeyCode.None,
        UiKeyKind.Char => UiKeyCode.Char,
        UiKeyKind.Enter => UiKeyCode.Enter,
        UiKeyKind.Tab => UiKeyCode.Tab,
        UiKeyKind.Backspace => UiKeyCode.Backspace,
        UiKeyKind.Escape => UiKeyCode.Escape,
        UiKeyKind.Up => UiKeyCode.Up,
        UiKeyKind.Down => UiKeyCode.Down,
        UiKeyKind.Left => UiKeyCode.Left,
        UiKeyKind.Right => UiKeyCode.Right,
        UiKeyKind.Home => UiKeyCode.Home,
        UiKeyKind.End => UiKeyCode.End,
        UiKeyKind.PageUp => UiKeyCode.PageUp,
        UiKeyKind.PageDown => UiKeyCode.PageDown,
        UiKeyKind.F1 => UiKeyCode.F1,
        UiKeyKind.F2 => UiKeyCode.F2,
        UiKeyKind.F3 => UiKeyCode.F3,
        UiKeyKind.F4 => UiKeyCode.F4,
        UiKeyKind.F5 => UiKeyCode.F5,
        UiKeyKind.F6 => UiKeyCode.F6,
        UiKeyKind.F7 => UiKeyCode.F7,
        UiKeyKind.F8 => UiKeyCode.F8,
        UiKeyKind.F9 => UiKeyCode.F9,
        UiKeyKind.F10 => UiKeyCode.F10,
        UiKeyKind.F11 => UiKeyCode.F11,
        UiKeyKind.F12 => UiKeyCode.F12,
        _ => UiKeyCode.None,
    };

    private static KeyModifierSet ToMods(UiKeyMods mods)
    {
        KeyModifierSet result = KeyModifierSet.None;
        if (mods.HasFlag(UiKeyMods.Shift)) result |= KeyModifierSet.Shift;
        if (mods.HasFlag(UiKeyMods.Ctrl)) result |= KeyModifierSet.Ctrl;
        if (mods.HasFlag(UiKeyMods.Alt)) result |= KeyModifierSet.Alt;
        return result;
    }
}
