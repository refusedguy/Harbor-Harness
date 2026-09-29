using Avalonia.Input;
using Harbor.Abstractions.Tools;
using Harbor.Ui.Framework.Navigation;
using Microsoft.Extensions.Logging;
namespace Harbor.App.Avalonia.Services;
/// <summary>
///     Centralised keyboard-shortcut dispatcher for the main window. Every
///     global key binding (Ctrl+P, Ctrl+B, Ctrl+Shift+T, Ctrl+O, Ctrl+S,
///     Ctrl+L, Esc) is routed through <see cref="HandleKeyDown" /> so the
///     MainWindow code-behind stays free of branching logic and the
///     shortcut table is unit-testable in isolation.
/// </summary>
/// <remarks>
///     Registered as a singleton in <c>AppHost</c> so tests can verify
///     "Ctrl+P opens the command palette" by calling
///     <see cref="HandleKeyDown" /> with a synthetic <see cref="KeyEventArgs" />
///     instead of driving a real Avalonia input pump.
/// </remarks>
/// <remarks>
///     <para>
///         <b>Why the shortcuts cannot await (§569).</b> <see cref="HandleKeyDown" />
///         returns <see cref="bool" />: Avalonia's <c>OnKeyDown</c> override has to
///         know synchronously whether the key was handled so it can set
///         <c>e.Handled</c> before the base call. Making it <c>async</c> would defer
///         that decision past the base call and change input behaviour, so the
///         contract stays synchronous.
///     </para>
///     <para>
///         That is why the two file commands route through
///         <see cref="TaskFireAndForget" /> rather than <c>await</c>: the sync
///         contract is not the defect, losing the fault is. Ctrl+S in particular
///         drives a <c>SaveAsync</c> whose result used to be dropped bare, so a
///         save that threw was invisible — no log, no toast. It now reports.
///     </para>
/// </remarks>
public sealed class KeyboardShortcutService
{
    private readonly IShellChrome _shellChrome;
    private readonly IWorkspaceCommands _workspaceCommands;
    private readonly IFloatingTerminals? _floatingTerminals;
    private readonly ILogger<KeyboardShortcutService> _logger;

    public KeyboardShortcutService(IShellChrome shellChrome, IWorkspaceCommands workspaceCommands,
        ILogger<KeyboardShortcutService> logger, IFloatingTerminals? floatingTerminals = null)
    {
        _shellChrome = shellChrome;
        _workspaceCommands = workspaceCommands;
        _logger = logger;
        _floatingTerminals = floatingTerminals;
    }

    public bool HandleKeyDown(KeyEventArgs e)
    {
        bool ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        bool shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);

        // Esc → close the topmost overlay via the shell chrome.
        if (e.Key == Key.Escape)
        {
            return _shellChrome.CloseTopOverlay();
        }

        // Ctrl+P / Ctrl+Shift+P → command palette.
        if (ctrl && e.Key == Key.P)
        {
            _shellChrome.OpenOverlay(OverlayIds.Palette);
            return true;
        }

        // Ctrl+B → toggle sidebar.
        if (ctrl && e.Key == Key.B)
        {
            _shellChrome.ToggleSidebar();
            return true;
        }

        // Ctrl+Shift+T → toggle theme.
        if (ctrl && shift && e.Key == Key.T)
        {
            _shellChrome.ToggleTheme();
            return true;
        }

        // Ctrl+O → open file.
        if (ctrl && e.Key == Key.O)
        {
            TaskFireAndForget.Forget(
                _workspaceCommands.OpenFileAsync(),
                ex => _logger.LogError(ex, "Ctrl+O open-file failed"));
            return true;
        }

        // Ctrl+S → save file.
        if (ctrl && e.Key == Key.S)
        {
            TaskFireAndForget.Forget(
                _workspaceCommands.SaveFileAsync(),
                ex => _logger.LogError(ex, "Ctrl+S save-file failed"));
            return true;
        }

        // Ctrl+L → clear chat.
        if (ctrl && e.Key == Key.L)
        {
            _workspaceCommands.ClearChat();
            return true;
        }

        // Ctrl+` → toggle the floating PTY terminal pane.
        if (ctrl && e.Key == Key.OemTilde && _floatingTerminals is not null)
        {
            _floatingTerminals.TogglePane();
            return true;
        }

        return false;
    }
}
