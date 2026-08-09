using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Harbor.App.Avalonia.ViewModels;
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
        if (ResolveMainViewModel() is { } main)
            main.IsFocusSessionOpen = false;
    }

    private MainViewModel? ResolveMainViewModel()
    {
        if (TopLevel.GetTopLevel(this) is Window window)
            return window.DataContext as MainViewModel;
        return null;
    }
}
