using Avalonia.Controls;
using Avalonia.Input;
namespace Harbor.App.Avalonia.Services;
/// <summary>
///     Encapsulates custom window-chrome behaviours for the main window —
///     drag-to-move, minimize, maximize/restore, and close. With
///     <c>ExtendClientAreaChromeHints=PreferNone</c> the native caption
///     buttons are gone, so the XAML draws its own styled buttons and
///     forwards their click events here.
/// </summary>
/// <remarks>
///     Registered as a singleton in <c>AppHost</c> so tests can mock the
///     chrome behaviour (e.g. assert that "close" was clicked without
///     actually tearing down a real OS window).
/// </remarks>
public sealed class WindowChromeService
{
    /// <summary>
    ///     Handle a pointer press on the custom title bar. Single click
    ///     begins a window drag (so the user can move the window by its
    ///     title bar); double-click toggles maximize, mirroring the
    ///     Win32 caption convention.
    /// </summary>
    /// <param name="window">The window whose title bar was pressed.</param>
    /// <param name="e">The pointer-pressed event args.</param>
    public void HandleTitleBarPointerPressed(Window window, PointerPressedEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            window.WindowState = window.WindowState == WindowState.Maximized
                ? WindowState.Normal
                : WindowState.Maximized;
            e.Handled = true;
            return;
        }

        window.BeginMoveDrag(e);
    }

    /// <summary>Minimize the window to the taskbar.</summary>
    /// <param name="window">The window to minimize.</param>
    public void Minimize(Window window) =>
        window.WindowState = WindowState.Minimized;

    /// <summary>Toggle the window between maximized and restored (normal).</summary>
    /// <param name="window">The window to toggle.</param>
    public void MaximizeOrRestore(Window window) =>
        window.WindowState = window.WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;

    /// <summary>Close the window via the standard <see cref="Window.Close" /> path.</summary>
    /// <param name="window">The window to close.</param>
    public void Close(Window window) => window.Close();
}
