using Avalonia;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Harbor.App.Avalonia.ViewModels;
using Harbor.Ui.Framework.Navigation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Harbor.App.Avalonia.Views.Shell;

public partial class ActivityRailView : UserControl
{
    private IShellChrome? _shellChrome;
    private IShellChrome ShellChrome => _shellChrome ??= App.Services.GetRequiredService<IShellChrome>();

    private CodeEditorViewModel? _codeEditor;
    private CodeEditorViewModel CodeEditor => _codeEditor ??= App.Services.GetRequiredService<CodeEditorViewModel>();

    private ILogger<ActivityRailView>? _logger;
    private ILogger<ActivityRailView> Logger => _logger ??= App.Services.GetRequiredService<ILogger<ActivityRailView>>();

    public ActivityRailView()
    {
        InitializeComponent();
        MinWidth = 56;

        DataContextChanged += (_, __) =>
        {
            if (DataContext is MainViewModel vm)
            {
                vm.PropertyChanged += (s, e) =>
                {
                    if (e.PropertyName == nameof(MainViewModel.IsSidebarVisible))
                    {
                        UpdateToggleButtonState();
                    }
                };
            }
        };
    }

    private void UpdateToggleButtonState()
    {
        if (Rail_ToggleButton is not { } button) return;

        if (button.RenderTransform is not RotateTransform icon)
        {
            icon = new RotateTransform();
            button.RenderTransform = icon;
        }

        var isExpanded = DataContext is MainViewModel vm && vm.IsSidebarVisible;
        icon.Angle = isExpanded ? 180 : 0;
        icon.CenterX = 8;
        icon.CenterY = 8;
    }

    private void MarkActive(object? sender)
    {
        if (sender is not Button clicked) return;

        foreach (var button in new[] { Rail_BoardButton, Rail_SearchButton, Rail_DiffButton })
        {
            if (button is null) continue;

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

    private void Toggle_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm)
        {
            vm.ToggleSidebar();
            UpdateToggleButtonState();
        }
    }

    private void Board_Click(object? sender, RoutedEventArgs e)
    {
        MarkActive(sender);
        ShellChrome.OpenOverlay(OverlayIds.SessionsFlyout);
    }

    private void Search_Click(object? sender, RoutedEventArgs e)
    {
        MarkActive(sender);
        ShellChrome.OpenOverlay(OverlayIds.Palette);
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
        ShellChrome.OpenOverlay(OverlayIds.Settings);
    }

    private async void Refresh_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;

        await vm.RefreshFileTreeAsync();
    }

    private async void FileTreeView_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is not TreeView treeView) return;

        if (treeView.SelectedItem is not FileTreeNode node || node.IsDirectory) return;

        try
        {
            if (DataContext is MainViewModel vm)
            {
                vm.SwitchView("code");
            }

            await CodeEditor.LoadFileAsync(node.FullPath);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to open file: {Path}", node.FullPath);
        }
    }
}

public sealed class BoolToWidthConverter : IValueConverter
{
    public double ExpandedWidth { get; set; } = 240;
    public double CollapsedWidth { get; set; } = 56;

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is bool b && b) return ExpandedWidth;
        return CollapsedWidth;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class FileTypeToGeometryConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not FileTreeNode node) return null;

        string pathData = node.IsDirectory
            ? (node.IsExpanded
                ? "M19 19H5V8h14v11zm0-15h-8l-2-2H5c-1.1 0-2 .9-2 2v12c0 1.1.9 2 2 2h14c1.1 0 2-.9 2-2V5c0-1.1-.9-2-2-2z"
                : "M10 4H4c-1.1 0-2 .9-2 2v12c0 1.1.9 2 2 2h16c1.1 0 2-.9 2-2V8c0-1.1-.9-2-2-2h-8l-2-2z")
            : "M9.4 16.6L6.8 14l2.6-2.6L8 10l-4 4 4 4 1.4-1.4zm5.2.6l2.6-2.6 2.6 2.6L18 17.2l2.6-2.6L18 12l2.6-2.6L18 8.8 15.4 11l-1.2-1.2L14 11l1.2 1.2-1.2 1.2L14 16l1.2-1.2 1.2 1.2-1.2 1.2zm1-7.4V4h5v10h-5v-2.8z";

        return Geometry.Parse(pathData);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
