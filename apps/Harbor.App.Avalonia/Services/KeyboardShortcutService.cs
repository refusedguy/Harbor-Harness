using Avalonia.Input;
using Harbor.App.Avalonia.ViewModels;
using Harbor.Ui.Framework.Services;
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
internal sealed class KeyboardShortcutService
{
    private readonly IOverlayStack _overlayStack;

    public KeyboardShortcutService()
        : this(new OverlayStackService())
    {
    }

    public KeyboardShortcutService(IOverlayStack overlayStack)
    {
        _overlayStack = overlayStack;
    }

    public bool HandleKeyDown(MainViewModel? vm, KeyEventArgs e)
    {
        if (vm is null) return false;

        bool ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        bool shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);

        // Esc closes the topmost overlay via the overlay stack.
        if (e.Key == Key.Escape)
        {
            var popped = _overlayStack.PopTop();
            if (popped is not null)
                return true;
            return false;
        }

        // Ctrl+P / Ctrl+Shift+P → command palette.
        if (ctrl && e.Key == Key.P)
        {
            vm.OpenCommandPaletteCommand.Execute(null);
            return true;
        }

        // Ctrl+B → toggle sidebar.
        if (ctrl && e.Key == Key.B)
        {
            vm.ToggleSidebarCommand.Execute(null);
            return true;
        }

        // Ctrl+Shift+T → toggle theme.
        if (ctrl && shift && e.Key == Key.T)
        {
            vm.ToggleThemeCommand.Execute(null);
            return true;
        }

        // Ctrl+O → open file.
        if (ctrl && e.Key == Key.O)
        {
            _ = vm.CodeEditor.OpenFileCommand.ExecuteAsync(null);
            return true;
        }

        // Ctrl+S → save file.
        if (ctrl && e.Key == Key.S)
        {
            _ = vm.CodeEditor.SaveCommand.ExecuteAsync(null);
            return true;
        }

        // Ctrl+L → clear chat.
        if (ctrl && e.Key == Key.L)
        {
            vm.Chat.ClearCommand.Execute(null);
            return true;
        }

        return false;
    }
}
