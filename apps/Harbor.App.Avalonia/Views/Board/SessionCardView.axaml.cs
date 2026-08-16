using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Harbor.App.Avalonia.ViewModels.Board;

namespace Harbor.App.Avalonia.Views.Board;

public partial class SessionCardView : UserControl
{
    public SessionCardView()
    {
        InitializeComponent();
    }

    private void OnCardPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is SessionCardViewModel vm)
            vm.SelectSessionCommand.Execute(null);
    }

    private void OnKebabPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        e.Handled = true;
    }
}
