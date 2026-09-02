using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Harbor.App.Avalonia.Services;
using Harbor.App.Avalonia.ViewModels;
using Harbor.Ui.Framework.Navigation;
using Microsoft.Extensions.DependencyInjection;

namespace Harbor.App.Avalonia.Views;
/// <summary>
///     Main window code-behind. Hosts the shell layout and forwards every
///     global input event (title-bar drag, caption buttons, keyboard
///     shortcuts) to dedicated services resolved from DI
///     (<see cref="WindowChromeService" /> and
///     <see cref="KeyboardShortcutService" />). The code-behind itself only
///     wires up services — no behaviour lives here. Disposes the bound
///     <see cref="MainViewModel" /> on close so the UiStore subscription
///     and toast continuations are torn down cleanly.
/// </summary>
/// <remarks>
///     <para>
///         Single-window HDS shell. The new DockPanel layout hosts
///         TitleBarView (44px), ActivityRail (56px), main content area,
///         RightDrawer overlay (360px), and StatusBarView (32px).
///     </para>
/// </remarks>
public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;
    private readonly KeyboardShortcutService _keyboard;
    private readonly WindowChromeService _chrome;
    private readonly IShellChrome _shellChrome;

    [ActivatorUtilitiesConstructor]
    public MainWindow(
        MainViewModel vm,
        KeyboardShortcutService keyboard,
        WindowChromeService chrome,
        IShellChrome shellChrome)
    {
        _vm = vm;
        _keyboard = keyboard;
        _chrome = chrome;
        _shellChrome = shellChrome;
        InitializeComponent();
        DataContext = vm;

        this.Closing += (_, _) =>
        {
            if (this.DataContext is IDisposable disposable)
            {
                disposable.Dispose();
            }
        };
    }

    public WindowChromeService ChromeService => _chrome;

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (_keyboard.HandleKeyDown(e))
        {
            e.Handled = true;
        }
        base.OnKeyDown(e);
    }

    private void Quit_Click(object? sender, RoutedEventArgs e) => _chrome.Close(this);

    private void ViewChat_Click(object? sender, RoutedEventArgs e) =>
        _vm.SwitchViewCommand.Execute("chat");

    private void ViewCode_Click(object? sender, RoutedEventArgs e) =>
        _vm.SwitchViewCommand.Execute("code");

    private void TitleBar_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        _chrome.HandleTitleBarPointerPressed(this, e);
    }

    private void Minimize_Click(object? sender, RoutedEventArgs e) => _chrome.Minimize(this);

    private void Maximize_Click(object? sender, RoutedEventArgs e) => _chrome.MaximizeOrRestore(this);

    private void Close_Click(object? sender, RoutedEventArgs e) => _chrome.Close(this);

    private void PickerBackdrop_Click(object? sender, PointerPressedEventArgs e)
    {
        if (ReferenceEquals(e.Source, sender) && _vm is { } vm)
        {
            _shellChrome.CloseOverlay(OverlayIds.ModelPicker);
        }
    }

    private void PickerClose_Click(object? sender, RoutedEventArgs e)
    {
        if (_vm is { } vm)
        {
            _shellChrome.CloseOverlay(OverlayIds.ModelPicker);
        }
    }
}
