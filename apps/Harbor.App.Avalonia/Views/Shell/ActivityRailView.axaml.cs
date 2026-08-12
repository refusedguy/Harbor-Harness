using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml.Styling;
using Harbor.App.Avalonia.Services;
using Harbor.App.Avalonia.ViewModels;
using Harbor.Desktop.Shared.Locators;
using Microsoft.Extensions.DependencyInjection;

namespace Harbor.App.Avalonia.Views.Shell;

public partial class ActivityRailView : UserControl
{
    public ActivityRailView()
    {
        InitializeComponent();
        MinWidth = 56;
    }

    private static T Locate<T>() where T : class =>
        App.Services is { } sp
            ? sp.GetRequiredService<IViewModelLocator>().Get<T>()
            : throw new InvalidOperationException("App.Services is not initialised.");

    private void Board_Click(object? sender, RoutedEventArgs e)
    {
        var vm = Locate<MainViewModel>();
        vm.SwitchViewCommand.Execute("board");
    }

    private void Search_Click(object? sender, RoutedEventArgs e)
    {
        var vm = Locate<MainViewModel>();
        vm.OpenCommandPaletteCommand.Execute(null);
    }

    private void Diff_Click(object? sender, RoutedEventArgs e)
    {
        var vm = Locate<MainViewModel>();
        vm.ToggleRightDrawerCommand.Execute("Diff");
    }

    private void Theme_Click(object? sender, RoutedEventArgs e)
    {
        var themeService = Locate<ThemeService>();
        var themes = new[] { "CatppuccinMocha", "Vapor", "Lumen", "Paper", "Mono" };

        string current = themes[0];
        if (Application.Current is { } app)
        {
            foreach (var dict in app.Resources.MergedDictionaries)
            {
                if (dict is ResourceInclude inc && inc.Source is { } src)
                {
                    var uri = src.OriginalString;
                    if (uri.StartsWith("avares://Harbor.App.Avalonia/Themes/Hds/", StringComparison.OrdinalIgnoreCase))
                    {
                        var name = uri.Split('/')[^1].Split('.')[0];
                        if (name is not ("BaseTokens" or "Icons" or "Typography"))
                        {
                            current = name;
                            break;
                        }
                    }
                }
            }
        }

        var index = Array.IndexOf(themes, current);
        var next = themes[(index + 1) % themes.Length];
        themeService.ApplyHds(next);
    }

    private void Settings_Click(object? sender, RoutedEventArgs e)
    {
        var vm = Locate<MainViewModel>();
        vm.OpenSettingsCommand.Execute(null);
    }
}
