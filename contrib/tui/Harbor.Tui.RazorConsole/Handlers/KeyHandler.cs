using Harbor.Ui.Framework.Rendering.Input;
using Harbor.Ui.Framework.State;
using Harbor.Abstractions.Models;
using Microsoft.Extensions.Logging;
namespace Harbor.Tui.RazorConsole.Handlers;
/// <summary>
///     Resolves a raw <see cref="ConsoleKeyInfo" /> to a framework-neutral
///     <see cref="UiKey" />, asks <see cref="ChatKeyMap" /> which
///     <see cref="ChatAction" /> it means, and dispatches the resulting
///     <see cref="AppMsg.KeyInput" /> into the <see cref="UiStore" /> this shell
///     owns. RazorConsole doesn't expose a public key event in its component
///     pipeline, so the bridge reads from <see cref="Console.ReadKey" /> in its
///     input loop and forwards each press here.
/// </summary>
/// <remarks>
///     The <c>ConsoleKey</c> → key-vocabulary table is NOT here: it lives once in
///     <see cref="ConsoleKeyMapper" /> (issue #554) and this class converts
///     through the shared <see cref="KeyEventAdapter" />. The two sibling shells
///     (Termina, TerminalGui) call the same mapper, so a key cannot mean one
///     action in RazorConsole and another in the backend the user switched to.
/// </remarks>
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
        // Single source of truth: ChatKeyMap owns every key→action mapping
        // (Clear, Abort, HelpPanel, JumpPalette incl. the LF alias). This shell
        // only translates the native key into UiKey and dispatches.
        var action = _keyMap.Resolve(key);

        if (action == ChatAction.None)
            return new TuiEffect.None();

        _logger?.LogTrace("Key {Key} → {Action}", info.Key, action);
        return _store.Dispatch(new AppMsg.KeyInput(action, key));
    }

    /// <summary>
    ///     Map a <see cref="ConsoleKeyInfo" /> to a framework-neutral <see cref="UiKey" />
    ///     through the shared console-key mapper.
    /// </summary>
    public static UiKey ToUiKey(ConsoleKeyInfo info) =>
        KeyEventAdapter.ToUiKey(ConsoleKeyMapper.FromConsoleKeyInfo(info));
}
