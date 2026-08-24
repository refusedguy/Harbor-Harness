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

    /// <summary>C4: the accent "active" rail state follows the last click —
    /// it used to be hardcoded on the sessions button regardless of use.</summary>
    private void MarkActive(object? sender)
    {
        if (sender is not Button clicked)
        {
            return;
        }

        foreach (var button in new[] { Rail_BoardButton, Rail_SearchButton, Rail_DiffButton })
        {
            if (button is null)
            {
                continue;
            }

            if (ReferenceEquals(button, clicked))
            {
                button.Classes.Add("RailButtonActive");
            }
            else
            {
                button.Classes.Remove("RailButtonActive");
            }
        }
    }

    private void Board_Click(object? sender, RoutedEventArgs e)
    {
        MarkActive(sender);
        ShellChrome.OpenOverlay("sessionsFlyout");
    }

    private void Search_Click(object? sender, RoutedEventArgs e)
    {
        MarkActive(sender);
        ShellChrome.OpenOverlay("palette");
    }

    private void Diff_Click(object? sender, RoutedEventArgs e)
    {
        MarkActive(sender);
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
