using Avalonia.Controls;
using Avalonia.Interactivity;
using Harbor.Ui.Framework.Navigation;
using Microsoft.Extensions.DependencyInjection;
namespace Harbor.App.Avalonia.Views.Shell;
/// <summary>
///     Right drawer host — slides in from the right when
///     <see cref="MainViewModel.IsRightDrawerOpen" /> is true.
/// </summary>
public partial class RightDrawerView : UserControl
{
    private IShellChrome? _shellChrome;
    private IShellChrome ShellChrome => _shellChrome ??= App.Services.GetRequiredService<IShellChrome>();

    private void Close_Click(object? sender, RoutedEventArgs e) =>
        ShellChrome.ToggleSidebar();
}
