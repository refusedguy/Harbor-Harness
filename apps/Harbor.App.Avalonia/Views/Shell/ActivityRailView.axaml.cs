using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml.Styling;
using Harbor.Ui.Framework.Navigation;
using Microsoft.Extensions.DependencyInjection;

namespace Harbor.App.Avalonia.Views.Shell;

public partial class ActivityRailView : UserControl
{
    public ActivityRailView()
    {
        InitializeComponent();
        MinWidth = 56;
    }

    private IShellChrome? _shellChrome;
    private IShellChrome ShellChrome => _shellChrome ??= App.Services.GetRequiredService<IShellChrome>();

    private void Board_Click(object? sender, RoutedEventArgs e)
    {
        ShellChrome.OpenOverlay("sessionsFlyout");
    }

    private void Search_Click(object? sender, RoutedEventArgs e)
    {
        ShellChrome.OpenOverlay("palette");
    }

    private void Diff_Click(object? sender, RoutedEventArgs e)
    {
        ShellChrome.ToggleSidebar();
    }

    private void Theme_Click(object? sender, RoutedEventArgs e)
    {
        ShellChrome.ToggleTheme();
    }

    private void Settings_Click(object? sender, RoutedEventArgs e)
    {
        ShellChrome.OpenOverlay("settings");
    }
}
