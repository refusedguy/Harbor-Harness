using Avalonia.Input;
using Harbor.Ui.Framework.Navigation;
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
public sealed class KeyboardShortcutService
{
    private readonly IShellChrome _shellChrome;
    private readonly IWorkspaceCommands _workspaceCommands;
    private readonly IFloatingTerminals? _floatingTerminals;

    public KeyboardShortcutService(IShellChrome shellChrome, IWorkspaceCommands workspaceCommands,
        IFloatingTerminals? floatingTerminals = null)
    {
        _shellChrome = shellChrome;
        _workspaceCommands = workspaceCommands;
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
            _ = _workspaceCommands.OpenFileAsync();
            return true;
        }

        // Ctrl+S → save file.
        if (ctrl && e.Key == Key.S)
        {
            _ = _workspaceCommands.SaveFileAsync();
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
