using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Harbor.Ui.Framework.Navigation;
using Microsoft.Extensions.DependencyInjection;
namespace Harbor.App.Avalonia.Views;

public partial class FocusSessionView : UserControl
{
    public FocusSessionView()
    {
        InitializeComponent();
    }

    private void Close_Click(object? sender, RoutedEventArgs e) => CloseModal();

    private void Backdrop_Click(object? sender, PointerPressedEventArgs e)
    {
        if (ReferenceEquals(e.Source, sender))
        {
            CloseModal();
        }
    }

    private void CloseModal()
    {
        ShellChrome.CloseOverlay("focusSession");
    }

    private IShellChrome? _shellChrome;
    private IShellChrome ShellChrome => _shellChrome ??= App.Services.GetRequiredService<IShellChrome>();
}
