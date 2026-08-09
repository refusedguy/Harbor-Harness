using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Harbor.App.Avalonia.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace Harbor.App.Avalonia.Views.Shell;

public partial class SessionsFlyoutView : UserControl
{
    public SessionsFlyoutView()
    {
        InitializeComponent();
        MinWidth = 280;
    }

    private void NewSession_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is SessionListViewModel vm)
            vm.NewSessionCommand.Execute(null);
    }
}