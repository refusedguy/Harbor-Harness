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
///     Ctrl+L / Ctrl+Enter / Esc).
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
}
