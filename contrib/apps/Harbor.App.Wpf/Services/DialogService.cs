using System.Windows;
using Harbor.App.Wpf.ViewModels;
using Harbor.App.Wpf.Views;
using Microsoft.Extensions.DependencyInjection;
namespace Harbor.App.Wpf.Services;
/// <summary>
///     Mediator for opening modal / non-modal dialogs from view-models.
///     Keeps the VMs decoupled from <see cref="Window" /> creation logic.
/// </summary>
public sealed class DialogService
{
    private readonly IServiceProvider _services;

    /// <summary>Construct a <see cref="DialogService" />.</summary>
    /// <param name="services">DI service provider used to resolve dialog windows.</param>
    public DialogService(IServiceProvider services)
    {
        _services = services;
    }

    /// <summary>
    ///     Show the provider browser modal.
    /// </summary>
    /// <returns><see langword="true" /> if the user confirmed; otherwise <see langword="false" />.</returns>
    public bool? ShowProviderBrowser()
    {
        var vm = _services.GetRequiredService<ProviderBrowserViewModel>();
        var view = _services.GetRequiredService<ProviderBrowserView>();
        view.DataContext = vm;
        return ShowModal(view, 900, 620);
    }

    /// <summary>
    ///     Show the settings modal.
    /// </summary>
    /// <returns><see langword="true" /> if the user applied changes; otherwise <see langword="false" />.</returns>
    public bool? ShowSettings()
    {
        var vm = _services.GetRequiredService<SettingsViewModel>();
        var view = _services.GetRequiredService<SettingsView>();
        view.DataContext = vm;
        return ShowModal(view, 760, 560);
    }

    /// <summary>
    ///     Show the command palette as a non-modal popup at the top of the screen.
    /// </summary>
    /// <param name="owner">Owning window (so the popup can be positioned relative to it).</param>
    public void ShowCommandPalette(Window owner)
    {
        var vm = _services.GetRequiredService<CommandPaletteViewModel>();
        var view = _services.GetRequiredService<CommandPaletteView>();
        view.DataContext = vm;
        view.Owner = owner;
        view.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        view.Width = 640;
        view.Height = 80;
        view.Show();
    }

    /// <summary>
    ///     Show a generic message-box dialog.
    /// </summary>
    /// <param name="title">Dialog title.</param>
    /// <param name="message">Body text.</param>
    /// <param name="kind">Message box kind (info, warning, error).</param>
    public void ShowMessage(string title, string message, MessageBoxImage kind = MessageBoxImage.Information) => MessageBox.Show(message, title, MessageBoxButton.OK, kind);

    /// <summary>
    ///     Show a yes/no confirmation dialog.
    /// </summary>
    /// <param name="title">Dialog title.</param>
    /// <param name="message">Body text.</param>
    /// <returns><see langword="true" /> if the user clicked Yes; otherwise <see langword="false" />.</returns>
    public bool Confirm(string title, string message) => MessageBox.Show(message, title, MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;

    private static bool? ShowModal(Window window, int width, int height)
    {
        window.Width = width;
        window.Height = height;
        window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        return window.ShowDialog();
    }
}
