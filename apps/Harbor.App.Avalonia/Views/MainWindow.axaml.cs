using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Harbor.App.Avalonia.ViewModels;

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
