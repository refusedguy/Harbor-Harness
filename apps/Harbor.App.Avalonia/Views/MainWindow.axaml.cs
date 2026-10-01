using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Harbor.App.Avalonia.Hosting;
using Harbor.App.Avalonia.Services;
using Harbor.App.Avalonia.ViewModels;
using Harbor.Desktop.Shared.Locators;
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
///     <para>
///         The window is also the <see cref="IShellLocatorHost" />: it is the one view
///         the composition root builds itself (<c>ActivatorUtilities.CreateInstance</c>
///         in <c>App.axaml.cs</c>), so it is the only place a real
///         <see cref="IViewModelLocator" /> can be injected. Every shell view declared in
///         XAML finds it by walking up the logical tree — see <see cref="ShellLocator" />.
///         That is how the twelve <c>App.Services.GetRequiredService</c> reads in the view
///         code-behinds were removed (#779) without adding a constructor parameter that
///         XAML would not supply.
///     </para>
/// </remarks>
public partial class MainWindow : Window, IShellLocatorHost
{
    private readonly MainViewModel _vm;
    private readonly KeyboardShortcutService _keyboard;
    private readonly WindowChromeService _chrome;
    private readonly IShellChrome _shellChrome;
    private readonly IViewModelLocator _locator;

    /// <inheritdoc />
    public IViewModelLocator Locator => _locator;

    [ActivatorUtilitiesConstructor]
    public MainWindow(
        MainViewModel vm,
        KeyboardShortcutService keyboard,
        WindowChromeService chrome,
        IShellChrome shellChrome,
        IViewModelLocator locator)
    {
        _vm = vm;
        _keyboard = keyboard;
        _chrome = chrome;
        _shellChrome = shellChrome;
        _locator = locator;
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
