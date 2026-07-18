using Avalonia.Controls;
using Avalonia.Interactivity;
using Harbor.App.Avalonia.ViewModels;

namespace Harbor.App.Avalonia.Views;

/// <summary>
///     Settings dialog code-behind.
/// </summary>
public partial class SettingsView : UserControl
{
    /// <summary>Construct the settings view.</summary>
    public SettingsView()
    {
        InitializeComponent();
    }

    private void Close_Click(object? sender, RoutedEventArgs e)
    {
        if (this.VisualRoot is Window window && window.DataContext is MainViewModel main)
        {
            main.IsSettingsOpen = false;
        }
    }
}
