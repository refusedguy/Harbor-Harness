using Harbor.Ui.Framework.State;
using Microsoft.Extensions.Logging;

namespace Harbor.Tui.Termina.Handlers;

/// <summary>
///     Maps a raw <see cref="ConsoleKeyInfo" /> into the framework-neutral
///     <see cref="UiKey" />, resolves the <see cref="ChatAction" /> via
///     <see cref="ChatKeyMap" />, and dispatches a <see cref="UiMsg.KeyInput" />
///     through the supplied <see cref="UiStore" />. Returns the resulting
///     <see cref="TuiEffect" /> so the caller can execute side-effects via
///     <c>TuiEffectHost</c>.
/// </summary>
public sealed class KeyHandler
{
    private readonly ChatKeyMap _keyMap = new();
    private readonly UiStore _store;
    private readonly ILogger? _logger;

    public KeyHandler(UiStore store, ILogger? logger = null)
    {
        _store = store;
        _logger = logger;
    }

    /// <summary>Process a key; returns the effect the host should run (or <c>None</c>).</summary>
    public TuiEffect Handle(ConsoleKeyInfo info)
    {
        var key = ToUiKey(info);
        var action = _keyMap.Resolve(key);

        // Ctrl+L → clear (most terminals report it as a character).
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
            _ => UiKeyCode.None
        };
        return new UiKey(code, mods);
    }
}
