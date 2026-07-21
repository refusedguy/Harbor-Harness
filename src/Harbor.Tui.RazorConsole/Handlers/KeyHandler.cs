using Harbor.Ui.Framework.State;
using Microsoft.Extensions.Logging;
namespace Harbor.Tui.RazorConsole.Handlers;
/// <summary>
///     Maps a raw <see cref="ConsoleKeyInfo" /> into the framework-neutral
///     <see cref="UiKey" />, resolves the <see cref="ChatAction" /> via
///     <see cref="ChatKeyMap" />, and dispatches a <see cref="UiMsg.KeyInput" />
///     through the supplied <see cref="UiStore" />. RazorConsole doesn't
///     expose a public key event in its component pipeline, so the bridge
///     reads from <see cref="Console.ReadKey" /> in its input loop and
///     forwards each press here.
/// </summary>
public sealed class KeyHandler
{
    private readonly ChatKeyMap _keyMap = new();
    private readonly ILogger? _logger;
    private readonly UiStore _store;

    public KeyHandler(UiStore store, ILogger? logger = null)
    {
        _store = store;
        _logger = logger;
    }

    /// <summary>Process a key; returns the effect the host should run.</summary>
    public TuiEffect Handle(ConsoleKeyInfo info)
    {
        var key = ToUiKey(info);
        var action = _keyMap.Resolve(key);

        if (key.Code == UiKeyCode.Char && key.Character == 'l' && key.Mods.HasFlag(KeyModifierSet.Ctrl))
            action = ChatAction.Clear;
        else if (key.Code == UiKeyCode.Char && key.Character == 'c' && key.Mods.HasFlag(KeyModifierSet.Ctrl))
            action = ChatAction.Abort;
        else if (key.Code == UiKeyCode.Char && key.Character == '?')
            action = ChatAction.HelpPanel;

        if (action == ChatAction.None)
            return new TuiEffect.None();

        _logger?.LogTrace("Key {Key} → {Action}", info.Key, action);
        return _store.Dispatch(new UiMsg.KeyInput(action, key));
    }

    /// <summary>Map a <see cref="ConsoleKeyInfo" /> to a framework-neutral <see cref="UiKey" />.</summary>
    public static UiKey ToUiKey(ConsoleKeyInfo info)
    {
        var mods = KeyModifierSet.None;
        if ((info.Modifiers & ConsoleModifiers.Shift) != 0) mods |= KeyModifierSet.Shift;
        if ((info.Modifiers & ConsoleModifiers.Control) != 0) mods |= KeyModifierSet.Ctrl;
        if ((info.Modifiers & ConsoleModifiers.Alt) != 0) mods |= KeyModifierSet.Alt;

        if (info.KeyChar is >= (char)32 and not (char)127)
            return UiKey.ForChar(info.KeyChar, mods);

        var code = info.Key switch
        {
            ConsoleKey.UpArrow => UiKeyCode.Up,
            ConsoleKey.DownArrow => UiKeyCode.Down,
            ConsoleKey.LeftArrow => UiKeyCode.Left,
            ConsoleKey.RightArrow => UiKeyCode.Right,
            ConsoleKey.PageUp => UiKeyCode.PageUp,
            ConsoleKey.PageDown => UiKeyCode.PageDown,
            ConsoleKey.Home => UiKeyCode.Home,
            ConsoleKey.End => UiKeyCode.End,
            ConsoleKey.Enter => UiKeyCode.Enter,
            ConsoleKey.Escape => UiKeyCode.Escape,
            ConsoleKey.Backspace => UiKeyCode.Backspace,
            ConsoleKey.Tab => UiKeyCode.Tab,
            ConsoleKey.F1 => UiKeyCode.F1,
            ConsoleKey.F2 => UiKeyCode.F2,
            ConsoleKey.F3 => UiKeyCode.F3,
            ConsoleKey.F4 => UiKeyCode.F4,
            ConsoleKey.F5 => UiKeyCode.F5,
            ConsoleKey.F6 => UiKeyCode.F6,
            ConsoleKey.F7 => UiKeyCode.F7,
            ConsoleKey.F8 => UiKeyCode.F8,
            ConsoleKey.F9 => UiKeyCode.F9,
            ConsoleKey.F10 => UiKeyCode.F10,
            ConsoleKey.F11 => UiKeyCode.F11,
            // F12 toggles the in-TUI diagnostics / logs panel (ChatAction.ToggleLogsPanel).
            // RazorConsole's TextInput component does not surface raw F12 to the
            // bridge; the documented user-facing escape hatch is /logs. See
            // docs/TUI_FEATURE_GAPS.md. The mapping is still provided so any
            // future F12 wiring through a custom key event will resolve correctly.
            ConsoleKey.F12 => UiKeyCode.F12,
            _ => UiKeyCode.None
        };
        return new UiKey(code, mods);
    }
}
