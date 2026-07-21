using Avalonia.Controls;
using Avalonia.Input;
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

    /// <summary>
    ///     Click on the backdrop (the dark scrim outside the card) closes the
    ///     modal — same behaviour as Esc. The card itself stops the
    ///     PointerPressed event from bubbling because its Background is set
    ///     (Avalonia only routes pointer events through controls with a
    ///     non-null background brush).
    /// </summary>
    private void Backdrop_Click(object? sender, PointerPressedEventArgs e)
    {
        // Only close when the click landed directly on the backdrop, not on a
        // child. e.Source is the original hit-test target; if it's the
        // backdrop Border itself, the click was outside the card.
        if (ReferenceEquals(e.Source, sender))
        {
            if (this.VisualRoot is Window window && window.DataContext is MainViewModel main)
            {
                main.IsSettingsOpen = false;
            }
        }
    }
}
