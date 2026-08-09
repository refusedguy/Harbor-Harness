using Avalonia.Controls;
using Avalonia.Interactivity;
using Harbor.App.Avalonia.ViewModels;
namespace Harbor.App.Avalonia.Views.Shell;
/// <summary>
///     Right drawer host — slides in from the right when
///     <see cref="MainViewModel.IsRightDrawerOpen" /> is true.
/// </summary>
public partial class RightDrawerView : UserControl
{
    private MainViewModel? Vm => DataContext as MainViewModel;

    private void Close_Click(object? sender, RoutedEventArgs e) =>
        Vm?.ToggleRightDrawerCommand.Execute(null);
}
