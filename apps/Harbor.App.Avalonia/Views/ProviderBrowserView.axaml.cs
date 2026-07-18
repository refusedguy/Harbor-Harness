using Avalonia.Controls;
using Avalonia.Interactivity;
using Harbor.App.Avalonia.ViewModels;

namespace Harbor.App.Avalonia.Views;

/// <summary>
///     Provider browser code-behind.
/// </summary>
public partial class ProviderBrowserView : UserControl
{
    /// <summary>Construct the provider browser.</summary>
    public ProviderBrowserView()
    {
        InitializeComponent();
        AttachedToVisualTree += async (_, _) =>
        {
            if (DataContext is ProviderBrowserViewModel vm)
            {
                await vm.LoadProvidersCommand.ExecuteAsync(null);
            }
        };
    }

    private void Close_Click(object? sender, RoutedEventArgs e)
    {
        if (this.VisualRoot is Window window && window.DataContext is MainViewModel main)
        {
            main.IsProviderBrowserOpen = false;
        }
    }

    private void Provider_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (DataContext is ProviderBrowserViewModel vm && vm.SelectedProvider is not null)
        {
            _ = vm.LoadModelsCommand.ExecuteAsync(vm.SelectedProvider);
        }
    }
}
