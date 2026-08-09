using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Harbor.App.Avalonia.Services;
using Microsoft.Extensions.DependencyInjection;
namespace Harbor.App.Avalonia.Views.Chrome;
/// <summary>
///     Mac-style title bar with traffic-light caption buttons.
/// </summary>
public partial class TitleBarView : UserControl
{
    private Window? HostWindow => TopLevel.GetTopLevel(this) as Window;

    private void TitleBar_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            var chrome = App.Services?.GetService<WindowChromeService>() ?? new WindowChromeService();
            chrome.MaximizeOrRestore(HostWindow!);
            e.Handled = true;
            return;
        }

        HostWindow?.BeginMoveDrag(e);
    }

    private void Minimize_Click(object? sender, RoutedEventArgs e)
    {
        var chrome = App.Services?.GetService<WindowChromeService>() ?? new WindowChromeService();
        chrome.Minimize(HostWindow!);
    }

    private void Maximize_Click(object? sender, RoutedEventArgs e)
    {
        var chrome = App.Services?.GetService<WindowChromeService>() ?? new WindowChromeService();
        chrome.MaximizeOrRestore(HostWindow!);
    }

    private void Close_Click(object? sender, RoutedEventArgs e)
    {
        var chrome = App.Services?.GetService<WindowChromeService>() ?? new WindowChromeService();
        chrome.Close(HostWindow!);
    }
}
