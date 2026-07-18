using System.Windows;
using System.Windows.Input;
using Harbor.App.Wpf.ViewModels;
using Harbor.App.Wpf.Services;

namespace Harbor.App.Wpf.Views;

/// <summary>
///     Main application window — 1200x800, sidebar + main + status bar.
///     Hosts the AvalonDock docking manager and the toast overlay.
/// </summary>
public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;
    private readonly WpfDispatcherAdapter? _dispatcher;

    /// <summary>Construct the <see cref="MainWindow" />.</summary>
    /// <param name="vm">Main view model.</param>
    /// <param name="dispatcher">Dispatcher adapter (optional).</param>
    /// <param name="theme">Theme service (subscribed for theme-changed notifications).</param>
    public MainWindow(MainViewModel vm, WpfDispatcherAdapter dispatcher, ThemeService theme)
    {
        _vm = vm;
        _dispatcher = dispatcher;
        DataContext = vm;
        InitializeComponent();
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        // Wire Ctrl+P command palette.
        InputBindings.Add(new KeyBinding(
            new RelayCommand(() => _vm.OpenCommandPaletteCommand.Execute(this)),
            Key.P, ModifierKeys.Control));

        // Wire Ctrl+T theme toggle.
        InputBindings.Add(new KeyBinding(
            new RelayCommand(() => _vm.ToggleThemeCommand.Execute(null)),
            Key.T, ModifierKeys.Control));
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        // Ctrl+P — open command palette.
        if (e.Key == Key.P && Keyboard.Modifiers == ModifierKeys.Control)
        {
            _vm.OpenCommandPaletteCommand.Execute(this);
            e.Handled = true;
        }
    }
}

/// <summary>
///     Simple relay command for use in XAML input bindings.
/// </summary>
public sealed class RelayCommand : ICommand
{
    private readonly Action _action;
    /// <summary>Construct a <see cref="RelayCommand" />.</summary>
    public RelayCommand(Action action) => _action = action;
    /// <inheritdoc />
    public bool CanExecute(object? parameter) => true;
    /// <inheritdoc />
    public void Execute(object? parameter) => _action();
    /// <inheritdoc />
    public event EventHandler? CanExecuteChanged;
}
