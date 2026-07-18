using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Harbor.App.Avalonia.Configuration;
using Harbor.App.Avalonia.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace Harbor.App.Avalonia.Views;

/// <summary>
///     Main window code-behind. Hosts the shell layout and the global keyboard
///     shortcuts (Ctrl+P / Ctrl+Shift+P / Ctrl+B / Ctrl+Shift+T / Ctrl+O / Ctrl+S /
///     Ctrl+L / Ctrl+Enter / Esc). Disposes the bound <see cref="MainViewModel"/>
///     on close so the UiStore subscription + toast continuations are torn down
///     cleanly — without this, the dispatcher subscription can keep the UI
///     thread busy after the window is gone (the "window won't close" hang).
/// </summary>
public partial class MainWindow : Window
{
    /// <summary>Construct the main window. Avalonia's generated InitializeComponent runs first.</summary>
    public MainWindow()
    {
        InitializeComponent();

        // Apply persisted window geometry from AvaloniaConfig (~/.harbor/avalonia.json).
        // The XAML defaults (Width=1280, Height=800) are the fallback when no config
        // is available yet (design-time previewer, or first-run before the config
        // file is written).
        ApplyConfiguredGeometry();

        // Dispose the MainViewModel when the window closes — this unsubscribes
        // the UiStore → dispatcher bridge and prevents background toast
        // continuations from racing the window teardown. Coupled with
        // App.OnShutdownRequested (which stops the IHost), this fixes the
        // "window hangs and won't close" symptom the user reported.
        Closing += (_, _) =>
        {
            if (DataContext is IDisposable disposable)
            {
                disposable.Dispose();
            }
        };
    }

    /// <summary>
    ///     Read <see cref="AvaloniaConfig.WindowWidth"/> / <see cref="AvaloniaConfig.WindowHeight"/>
    ///     / <see cref="AvaloniaConfig.WindowMaximized"/> from DI and apply them to this
    ///     window. No-op when <see cref="App.Services"/> is null (design-time).
    /// </summary>
    private void ApplyConfiguredGeometry()
    {
        var services = App.Services;
        if (services is null) return;
        var config = services.GetService<AvaloniaConfig>();
        if (config is null) return;

        if (config.WindowWidth > 0) Width = config.WindowWidth;
        if (config.WindowHeight > 0) Height = config.WindowHeight;
        if (config.WindowMaximized)
        {
            WindowState = WindowState.Maximized;
        }
    }

    private MainViewModel? Vm => DataContext as MainViewModel;

    /// <inheritdoc />
    protected override void OnKeyDown(KeyEventArgs e)
    {
        var vm = Vm;
        if (vm is null)
        {
            base.OnKeyDown(e);
            return;
        }

        bool ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        bool shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);

        // Esc closes the topmost open modal.
        if (e.Key == Key.Escape)
        {
            if (vm.IsCommandPaletteOpen) { vm.IsCommandPaletteOpen = false; e.Handled = true; }
            else if (vm.IsSettingsOpen) { vm.IsSettingsOpen = false; e.Handled = true; }
            else if (vm.IsProviderBrowserOpen) { vm.IsProviderBrowserOpen = false; e.Handled = true; }
            else if (vm.IsDiffOpen) { vm.IsDiffOpen = false; e.Handled = true; }
            else if (vm.IsTokenUsageOpen) { vm.IsTokenUsageOpen = false; e.Handled = true; }
            base.OnKeyDown(e);
            return;
        }

        // Ctrl+P / Ctrl+Shift+P → command palette.
        if (ctrl && e.Key == Key.P)
        {
            vm.OpenCommandPaletteCommand.Execute(null);
            e.Handled = true;
            base.OnKeyDown(e);
            return;
        }

        // Ctrl+B → toggle sidebar.
        if (ctrl && e.Key == Key.B)
        {
            vm.ToggleSidebarCommand.Execute(null);
            e.Handled = true;
            base.OnKeyDown(e);
            return;
        }

        // Ctrl+Shift+T → toggle theme.
        if (ctrl && shift && e.Key == Key.T)
        {
            vm.ToggleThemeCommand.Execute(null);
            e.Handled = true;
            base.OnKeyDown(e);
            return;
        }

        // Ctrl+O → open file.
        if (ctrl && e.Key == Key.O)
        {
            _ = vm.CodeEditor.OpenFileCommand.ExecuteAsync(null);
            e.Handled = true;
            base.OnKeyDown(e);
            return;
        }

        // Ctrl+S → save file.
        if (ctrl && e.Key == Key.S)
        {
            _ = vm.CodeEditor.SaveCommand.ExecuteAsync(null);
            e.Handled = true;
            base.OnKeyDown(e);
            return;
        }

        // Ctrl+L → clear chat.
        if (ctrl && e.Key == Key.L)
        {
            vm.Chat.ClearCommand.Execute(null);
            e.Handled = true;
            base.OnKeyDown(e);
            return;
        }

        base.OnKeyDown(e);
    }

    private void Quit_Click(object? sender, RoutedEventArgs e) => Close();

    private void ViewChat_Click(object? sender, RoutedEventArgs e) =>
        Vm?.SwitchViewCommand.Execute("chat");

    private void ViewCode_Click(object? sender, RoutedEventArgs e) =>
        Vm?.SwitchViewCommand.Execute("code");

    // ── Custom Windows title bar handlers (ExtendClientAreaToDecorationsHint) ──
    //
    // With ExtendClientAreaChromeHints=PreferNone, the native Windows caption
    // buttons are gone. We draw our own in MainWindow.axaml (Button.WindowButton
    // classes styled in Themes/AppStyles.axaml) and wire up the three behaviours
    // here. This kills the "ugly Windows bar that ruins everything" symptom the
    // user reported — the bar now matches the Catppuccin-Mocha theme instead of
    // the default white Windows bar.

    /// <summary>
    ///     Begin a window drag when the user presses the mouse on the custom
    ///     title bar. Buttons inside the bar swallow the event (Button eats
    ///     PointerPressed) so caption clicks never start a drag.
    /// </summary>
    private void TitleBar_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            // Double-click anywhere on the bar → toggle maximize, like Win32.
            WindowState = WindowState == WindowState.Maximized
                ? WindowState.Normal
                : WindowState.Maximized;
            e.Handled = true;
            return;
        }

        BeginMoveDrag(e);
    }

    /// <summary>Minimize button — collapses to the taskbar.</summary>
    private void Minimize_Click(object? sender, RoutedEventArgs e) =>
        WindowState = WindowState.Minimized;

    /// <summary>Maximize / restore button — toggles the maximized state.</summary>
    private void Maximize_Click(object? sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;

    /// <summary>Close button — triggers the standard Window.Close path.</summary>
    private void Close_Click(object? sender, RoutedEventArgs e) => Close();
}
