using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Harbor.App.Avalonia.Configuration;
using Harbor.App.Avalonia.Services;
using Harbor.App.Avalonia.ViewModels;
using Harbor.Desktop.Shared.Locators;
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
    public MainWindow()
    {
        InitializeComponent();

        this.Closing += (_, _) =>
        {
            if (this.DataContext is IDisposable disposable)
            {
                disposable.Dispose();
            }
        };
    }

    private MainViewModel? Vm => this.DataContext as MainViewModel;

    private static T Locate<T>() where T : class, new() =>
        App.Services is { } sp
            ? sp.GetRequiredService<IViewModelLocator>().Get<T>()
            : new T();

    protected override void OnKeyDown(KeyEventArgs e)
    {
        var keyboard = Locate<KeyboardShortcutService>();
        if (keyboard.HandleKeyDown(Vm, e))
        {
            e.Handled = true;
        }
        base.OnKeyDown(e);
    }

    private void Quit_Click(object? sender, RoutedEventArgs e)
    {
        var chrome = Locate<WindowChromeService>();
        chrome.Close(this);
    }

    private void ViewChat_Click(object? sender, RoutedEventArgs e) =>
        Vm?.SwitchViewCommand.Execute("chat");

    private void ViewCode_Click(object? sender, RoutedEventArgs e) =>
        Vm?.SwitchViewCommand.Execute("code");

    private void TitleBar_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var chrome = Locate<WindowChromeService>();
        chrome.HandleTitleBarPointerPressed(this, e);
    }

    private void Minimize_Click(object? sender, RoutedEventArgs e)
    {
        var chrome = Locate<WindowChromeService>();
        chrome.Minimize(this);
    }

    private void Maximize_Click(object? sender, RoutedEventArgs e)
    {
        var chrome = Locate<WindowChromeService>();
        chrome.MaximizeOrRestore(this);
    }

    private void Close_Click(object? sender, RoutedEventArgs e)
    {
        var chrome = Locate<WindowChromeService>();
        chrome.Close(this);
    }

    private void PickerBackdrop_Click(object? sender, PointerPressedEventArgs e)
    {
        if (ReferenceEquals(e.Source, sender) && Vm is { } vm)
        {
            vm.IsModelPickerOpen = false;
        }
    }

    private void PickerClose_Click(object? sender, RoutedEventArgs e)
    {
        if (Vm is { } vm)
        {
            vm.IsModelPickerOpen = false;
        }
    }
}
