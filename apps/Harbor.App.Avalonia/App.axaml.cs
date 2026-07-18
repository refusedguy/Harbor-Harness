using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Harbor.App.Avalonia.Configuration;
using Harbor.App.Avalonia.Services;
using Harbor.App.Avalonia.ViewModels;
using Harbor.App.Avalonia.Views;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ShutdownMode = global::Avalonia.Controls.ShutdownMode;

namespace Harbor.App.Avalonia;

/// <summary>
///     Avalonia <see cref="Application"/> subclass. The composition root for the
///     UI layer: receives the DI container from <see cref="AppHost.BuildAsync"/>
///     (set on <see cref="Services"/> by <c>Program.cs</c>'s AfterSetup callback),
///     constructs the <see cref="MainViewModel"/>, and shows the
///     <see cref="MainWindow"/>.
/// </summary>
public class App : Application
{
    /// <summary>
    ///     The DI container. Set by <c>Program.cs</c> in the Avalonia
    ///     <c>AfterSetup</c> callback before <see cref="OnFrameworkInitializationCompleted"/>
    ///     runs.
    /// </summary>
    public static IServiceProvider Services { get; set; } = null!;

    /// <inheritdoc />
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    /// <inheritdoc />
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;

            var logger = Services.GetRequiredService<ILogger<App>>();
            logger.LogInformation("Harbor.App.Avalonia starting — building MainViewModel");

            var mainViewModel = Services.GetRequiredService<MainViewModel>();
            var themeService = Services.GetRequiredService<ThemeService>();
            themeService.Application = this;


            var toastService = Services.GetRequiredService<ToastService>();
            toastService.ToastAdded += (_, toast) => mainViewModel.AddToast(toast);

            var mainWindow = new MainWindow
            {
                DataContext = mainViewModel,
            };
            desktop.MainWindow = mainWindow;
        }

        base.OnFrameworkInitializationCompleted();
    }
}
