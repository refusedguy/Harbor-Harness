using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Harbor.Ui.Framework.Navigation;
using Microsoft.Extensions.DependencyInjection;
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

    private void Close_Click(object? sender, RoutedEventArgs e) => CloseModal();

    /// <summary>
    ///     Click on the backdrop (the dark scrim outside the card) closes the
    ///     modal — same behaviour as Esc and the Close button. The card itself
    ///     stops the PointerPressed event from bubbling because its Background
    ///     is set (Avalonia only routes pointer events through controls with a
    ///     non-null background brush).
    /// </summary>
    private void Backdrop_Click(object? sender, PointerPressedEventArgs e)
    {
        if (ReferenceEquals(e.Source, sender))
        {
            CloseModal();
        }
    }

    private void CloseModal()
    {
        ShellChrome.CloseOverlay("diff");
    }

    private IShellChrome? _shellChrome;
    private IShellChrome ShellChrome => _shellChrome ??= App.Services.GetRequiredService<IShellChrome>();
}
