namespace Harbor.Ui.Framework.Rendering.Input;

/// <summary>
///     Single translation point from a BCL <see cref="ConsoleKeyInfo" /> into the
///     BCL-only <see cref="UiKeyDto" /> vocabulary. The State-side
///     <c>KeyEventAdapter</c> turns that DTO into <c>UiKey</c> /
///     <c>AppMsg.KeyInput</c>, so one physical key means the same action in
///     every renderer — the property <see cref="KeyEventMapper" /> exists to
///     guarantee for the kitty-protocol shells.
/// </summary>
/// <remarks>
///     <para>
///         This class is the ONLY place in the repository that names a
///         <see cref="ConsoleKey" /> member. The RazorConsole / Termina /
///         TerminalGui contrib shells each carried a byte-identical copy of the
///         switch; one of them documented itself as "the single source of truth
///         for key routing" while two siblings held the same table (issue #554).
///         They call this instead, so a binding can no longer be added to two
///         shells and forgotten in the third.
///     </para>
///     <para>
///         The mapping is the framework half only: resolving a DTO to a
///         <c>ChatAction</c> stays with <c>ChatKeyMap</c>, so a shell never adds
///         a key→action branch of its own. Lossy corners are explicit: a key
///         whose <see cref="ConsoleKeyInfo.KeyChar" /> is a control byte
///         (Ctrl+C arrives as 0x03) and whose <see cref="ConsoleKeyInfo.Key" />
///         is a letter resolves to <see cref="UiKeyKind.None" />, exactly as it
///         did in the three copies this replaced.
///     </para>
/// </remarks>
public static class ConsoleKeyMapper
{
    /// <summary>Translate a raw console key press into the key vocabulary.</summary>
    public static UiKeyDto FromConsoleKeyInfo(ConsoleKeyInfo info)
    {
        UiKeyMods mods = UiKeyMods.None;
        if ((info.Modifiers & ConsoleModifiers.Shift) != 0) mods |= UiKeyMods.Shift;
        if ((info.Modifiers & ConsoleModifiers.Control) != 0) mods |= UiKeyMods.Ctrl;
        if ((info.Modifiers & ConsoleModifiers.Alt) != 0) mods |= UiKeyMods.Alt;

        // LF (0x0A) is how some terminals report Ctrl+J (no Ctrl flag, Key=J or
        // Enter with a line-feed char). Preserve it as a character so the central
        // ChatKeyMap resolves it to JumpPalette; plain Enter arrives as '\r'.
        if (info.KeyChar == '\n')
        {
            return UiKeyDto.ForChar('\n', mods);
        }

        if (info.KeyChar is >= (char)32 and not (char)127)
        {
            return UiKeyDto.ForChar(info.KeyChar, mods);
        }

        UiKeyKind kind = info.Key switch
        {
            ConsoleKey.UpArrow => UiKeyKind.Up,
            ConsoleKey.DownArrow => UiKeyKind.Down,
            ConsoleKey.LeftArrow => UiKeyKind.Left,
            ConsoleKey.RightArrow => UiKeyKind.Right,
            ConsoleKey.PageUp => UiKeyKind.PageUp,
            ConsoleKey.PageDown => UiKeyKind.PageDown,
            ConsoleKey.Home => UiKeyKind.Home,
            ConsoleKey.End => UiKeyKind.End,
            ConsoleKey.Enter => UiKeyKind.Enter,
            ConsoleKey.Escape => UiKeyKind.Escape,
            ConsoleKey.Backspace => UiKeyKind.Backspace,
            ConsoleKey.Tab => UiKeyKind.Tab,
            ConsoleKey.F1 => UiKeyKind.F1,
            ConsoleKey.F2 => UiKeyKind.F2,
            ConsoleKey.F3 => UiKeyKind.F3,
            ConsoleKey.F4 => UiKeyKind.F4,
            ConsoleKey.F5 => UiKeyKind.F5,
            ConsoleKey.F6 => UiKeyKind.F6,
            ConsoleKey.F7 => UiKeyKind.F7,
            ConsoleKey.F8 => UiKeyKind.F8,
            ConsoleKey.F9 => UiKeyKind.F9,
            ConsoleKey.F10 => UiKeyKind.F10,
            ConsoleKey.F11 => UiKeyKind.F11,
            // F12 toggles the in-TUI diagnostics / logs panel
            // (ChatAction.ToggleLogsPanel). Shells that cannot deliver a raw
            // F12 to the bridge document the user-facing escape hatch
            // themselves (see RazorConsoleTeaBridge).
            ConsoleKey.F12 => UiKeyKind.F12,
            _ => UiKeyKind.None,
        };

        return new UiKeyDto(kind, mods);
    }
}
