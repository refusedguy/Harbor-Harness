using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Harbor.App.Avalonia.ViewModels;
using Harbor.Ui.Framework.Navigation;
using Microsoft.Extensions.DependencyInjection;
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
        ShellChrome.CloseOverlay(OverlayIds.Settings);
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsViewModel vm)
            vm.CancelCommand.Execute(null);

        CloseModal();
    }

    /// <summary>
    ///     Close the modal when the user clicks the dark backdrop outside
    ///     the card. Only fires when the click landed directly on the
    ///     backdrop, not on a child (the card eats the event).
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
        ShellChrome.CloseOverlay(OverlayIds.Settings);
    }

    private void OnThemePreviewClick(object? sender, PointerPressedEventArgs e)
    {
        var current = e.Source as Visual;
        while (current is not null)
        {
            if (current is Border { DataContext: ThemeSettingsViewModel.ThemePreviewModel preview })
            {
                if (DataContext is ThemeSettingsViewModel vm)
                    vm.ApplyHdsThemeCommand.Execute(preview.Name);
                break;
            }
            current = current.GetVisualParent();
        }
    }

    private IShellChrome? _shellChrome;
    private IShellChrome ShellChrome => _shellChrome ??= App.Services.GetRequiredService<IShellChrome>();
}
