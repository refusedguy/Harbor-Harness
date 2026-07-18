using Avalonia.Controls;
using Avalonia.Interactivity;
using Harbor.App.Avalonia.ViewModels;

namespace Harbor.App.Avalonia.Views;

/// <summary>
///     Token usage view code-behind. Closes itself via the parent MainViewModel.
/// </summary>
public partial class TokenUsageView : UserControl
{
    /// <summary>Construct the token-usage view.</summary>
    public TokenUsageView()
    {
        InitializeComponent();
    }

    private void Close_Click(object? sender, RoutedEventArgs e)
    {
        if (this.VisualRoot is Window window && window.DataContext is MainViewModel main)
        {
            main.IsTokenUsageOpen = false;
        }
    }
}
