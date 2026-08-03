using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Harbor.App.Avalonia.ViewModels;
using Harbor.App.Avalonia.ViewModels.Shell;
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
        if (ResolveMainViewModel() is { } main)
            main.IsSettingsOpen = false;
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
        if (ResolveMainViewModel() is { } main)
            main.IsSettingsOpen = false;
    }

    private MainViewModel? ResolveMainViewModel()
    {
        if (TopLevel.GetTopLevel(this) is Window window)
        {
            return window.DataContext switch
            {
                MainViewModel vm => vm,
                OrcaShellViewModel orca => orca.Main,
                _ => null
            };
        }

        return null;
    }
}
