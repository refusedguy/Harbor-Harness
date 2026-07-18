using Avalonia.Controls;
using Avalonia.Interactivity;
using Harbor.App.Avalonia.ViewModels;

namespace Harbor.App.Avalonia.Views;

/// <summary>
///     Diff view code-behind. Closes itself via the parent MainViewModel.
/// </summary>
public partial class DiffView : UserControl
{
    /// <summary>Construct the diff view.</summary>
    public DiffView()
    {
        InitializeComponent();
    }

    private void Close_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is DiffViewModel)
        {
            // Walk up to the MainWindow's MainViewModel.
            var window = this.VisualRoot as Window;
            if (window?.DataContext is MainViewModel main)
            {
                main.IsDiffOpen = false;
            }
        }
    }
}
