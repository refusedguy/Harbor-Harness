using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Harbor.App.Avalonia.Hosting;
using Harbor.App.Avalonia.Services;
using Harbor.App.Avalonia.ViewModels;
using Harbor.Ui.Framework.Navigation;
using Microsoft.Extensions.DependencyInjection;
namespace Harbor.App.Avalonia.Views.Shell;
/// <summary>
///     Right drawer host — slides in from the right when
///     <see cref="MainViewModel.IsRightDrawerOpen" /> is true.
/// </summary>
public partial class RightDrawerView : UserControl
{
    public RightDrawerView()
    {
        InitializeComponent();
    }

    private IShellChrome? _shellChrome;
    private IShellChrome ShellChrome => _shellChrome ??= ShellLocator.Of(this).Get<IShellChrome>();

    private WindowChromeService? _chrome;
    private WindowChromeService Chrome => _chrome ??= ShellLocator.Of(this).Get<WindowChromeService>();

    private void Close_Click(object? sender, RoutedEventArgs e) =>
        ShellChrome.ToggleSidebar();

    // K17: resize grip. The width band and the snap decision live in
    // PanelSnapRule / WindowChromeService; this code only translates pointer
    // deltas into a width and applies the result to the bound MainViewModel,
    // exactly the way ActivityRailView casts its DataContext for the toggle.
    private bool _resizing;
    private double _dragStartX;
    private double _dragStartWidth;

    private void ResizeGrip_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;

        _resizing = true;
        _dragStartX = e.GetPosition(this).X;
        _dragStartWidth = vm.DrawerWidth;
        if (sender is Control grip) e.Pointer.Capture(grip);
        e.Handled = true;
    }

    private void ResizeGrip_PointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_resizing || DataContext is not MainViewModel vm) return;

        // Dragging the left grip toward the screen centre widens the drawer.
        double proposed = _dragStartWidth + (_dragStartX - e.GetPosition(this).X);
        vm.DrawerWidth = Chrome.ClampDrawerDragWidth(proposed);
        e.Handled = true;
    }

    private void ResizeGrip_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_resizing) return;
        _resizing = false;

        if (DataContext is MainViewModel vm)
        {
            PanelSnapResult snap = Chrome.ResolveDrawerSnap(vm.DrawerWidth);
            vm.DrawerWidth = snap.Width;
            vm.IsRightDrawerOpen = snap.IsOpen;
        }

        e.Pointer.Capture(null);
        e.Handled = true;
    }
}
