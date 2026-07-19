using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Harbor.App.Avalonia.Configuration;
using Harbor.App.Avalonia.Services;
using Harbor.App.Avalonia.ViewModels;
using Harbor.App.Avalonia.ViewModels.Shell;
using Harbor.App.Avalonia.Views;
using Harbor.App.Avalonia.Views.Shell;
using Harbor.Desktop.Abstractions.Configuration;
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

    /// <summary>
    ///     The host built by <c>AppHost.BuildAsync</c>. Held so we can stop it
    ///     cleanly on app exit (cancels the agent loop, flushes the session
    ///     store, disposes the DI container). Set by <c>Program.cs</c>.
    /// </summary>
    public static Microsoft.Extensions.Hosting.IHost? Host { get; set; }

    /// <summary>
    ///     Shell layout mode — <c>"classic"</c> (default) or <c>"orca"</c>
    ///     (experimental Orca-inspired Harbor Desktop Shell).
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Set by <c>Program.cs</c> from the <c>--shell</c> CLI arg OR the
    ///         <c>HARBOR_SHELL</c> env var, BEFORE the Avalonia lifetime
    ///         starts. <c>MainWindow</c> reads this in its constructor to
    ///         decide between the classic Catppuccin-Mocha layout and the
    ///         Orca shell root view.
    ///     </para>
    ///     <para>
    ///         The E2E test driver (<c>HeadlessAvaloniaDriver</c>) sets this
    ///         directly via reflection before initializing the app, so the
    ///         Orca E2E test can opt in without spinning up a second process.
    ///     </para>
    /// </remarks>
    public static string ShellMode { get; set; } = "orca";

    /// <summary>
    ///     Convenience flag: <c>true</c> when <see cref="ShellMode"/> is
    ///     <c>"orca"</c>.
    /// </summary>
    public static bool IsOrcaShell => string.Equals(ShellMode, "orca", StringComparison.OrdinalIgnoreCase);

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

            // Stop the DI host cleanly when the desktop requests shutdown.
            // Without this, background tasks (agent loop, IPC pipe, event-bus
            // listeners) keep the process alive after the main window closes —
            // which is the "window won't close" hang the user reported.
            desktop.ShutdownRequested += OnShutdownRequested;

            var logger = Services.GetRequiredService<ILogger<App>>();
            logger.LogInformation("Harbor.App.Avalonia starting — building MainViewModel");

            var themeService = Services.GetRequiredService<ThemeService>();
            themeService.Application = this;

            // Apply the persisted theme + window geometry from AvaloniaConfig
            // (~/.harbor/avalonia.json) before the MainWindow is shown — this
            // avoids a visible flash of the default theme/size on launch.
            var config = Services.GetRequiredService<AvaloniaConfig>();
            themeService.ApplyFromConfig(config);

            var commonConfig = Services.GetRequiredService<CommonConfig>();

            // First-launch onboarding: when the common config has
            // OnboardingCompleted=false (the default for a fresh install), OR
            // when ~/.harbor/config.json doesn't exist yet, show the wizard
            // before the main window. The wizard writes OnboardingCompleted=true
            // on finish so subsequent launches skip it. The user can also re-run
            // it from Settings (which sets the flag back to false and restarts).
            bool configExists = File.Exists(commonConfig.ConfigFilePath);
            bool needsOnboarding = !configExists || !commonConfig.OnboardingCompleted;
            if (needsOnboarding)
            {
                logger.LogInformation("Onboarding required (configExists={Exists}, completed={Completed}) — showing wizard",
                    configExists, commonConfig.OnboardingCompleted);
                ShowOnboardingThenMain(desktop);
            }
            else
            {
                ShowMain(desktop);
            }
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    ///     Show the onboarding wizard as a modal child of the (hidden) main
    ///     window. When the wizard completes, the main window becomes visible.
    ///     If the user cancels, the app exits.
    /// </summary>
    private void ShowOnboardingThenMain(IClassicDesktopStyleApplicationLifetime desktop)
    {
        // Build the main window up-front so the onboarding dialog has a parent
        // for ShowDialog. We keep it hidden until the wizard finishes.
        var mainViewModel = Services.GetRequiredService<MainViewModel>();
        var toastService = Services.GetRequiredService<ToastService>();
        toastService.ToastAdded += (_, toast) => mainViewModel.AddToast(toast);

        // In Orca shell mode, the MainWindow's DataContext is an
        // OrcaShellViewModel wrapping MainViewModel; in classic mode it's the
        // MainViewModel directly. MainWindow.axaml.cs checks App.IsOrcaShell
        // in its constructor and swaps its Content to OrcaShellView accordingly.
        object windowDataContext = IsOrcaShell
            ? Services.GetRequiredService<OrcaShellViewModel>()
            : mainViewModel;

        var mainWindow = new MainWindow
        {
            DataContext = windowDataContext,
            IsVisible = false, // hidden until onboarding completes
        };
        desktop.MainWindow = mainWindow;

        var onboardingVm = Services.GetRequiredService<OnboardingViewModel>();
        var onboardingWindow = new OnboardingWindow();
        onboardingWindow.Bind(onboardingVm);

        // ShowDialog must run after the parent window has been initialized by
        // the dispatcher. Use Dispatcher.UIThread.Post to defer it one tick.
        Dispatcher.UIThread.Post(async () =>
        {
            await onboardingWindow.ShowDialog<OnboardingWindow.OnboardingResult>(mainWindow);
            if (onboardingWindow.DataContext is OnboardingViewModel vm && vm.IsCompleted)
            {
                // Either the user finished the wizard OR clicked Skip — either
                // way, open the main window. (Skip leaves OnboardingCompleted
                // false in the persisted config, so the wizard will reappear
                // next launch — that matches user intent: "I'll do this later".)

                // Rebind the agent to the wizard's saved provider/model. The
                // agent was initialized in AppHost.BuildAsync with the pre-wizard
                // CommonConfig defaults — the wizard just wrote a new config with
                // the user's selections, so we reload and rebind here. This makes
                // "kilocode/tencent/hy3:free" work immediately after the wizard
                // without requiring an app restart.
                var sessionManager = Services.GetRequiredService<SessionManager>();
                try
                {
                    // After wizard: rebind to the new config AND refresh session list.
                    // RebindFromCommonConfigAsync will create a new session if none exists
                    // (using the fresh CommonConfig from disk with the wizard's selections).
                    await sessionManager.RebindFromCommonConfigAsync();

                    // Refresh the session list so the sidebar shows existing sessions.
                    var mainVm = Services.GetRequiredService<MainViewModel>();
                    mainVm.Sessions.RefreshCommand.Execute(null);
                }
                catch (Exception ex)
                {
                    Services.GetService<ILogger<App>>()?.LogWarning(ex, "RebindFromCommonConfig after onboarding failed");
                }

                mainWindow.Show();
            }
            else
            {
                // User cancelled via window-X — exit the app.
                desktop.Shutdown();
            }
        });
    }

    /// <summary>Show the main window directly (onboarding already completed previously).</summary>
    private void ShowMain(IClassicDesktopStyleApplicationLifetime desktop)
    {
        var mainViewModel = Services.GetRequiredService<MainViewModel>();
        var toastService = Services.GetRequiredService<ToastService>();
        toastService.ToastAdded += (_, toast) => mainViewModel.AddToast(toast);

        // In Orca shell mode, the MainWindow's DataContext is an
        // OrcaShellViewModel wrapping MainViewModel; in classic mode it's the
        // MainViewModel directly. MainWindow.axaml.cs checks App.IsOrcaShell
        // in its constructor and swaps its Content to OrcaShellView accordingly.
        object windowDataContext = IsOrcaShell
            ? Services.GetRequiredService<OrcaShellViewModel>()
            : mainViewModel;

        var mainWindow = new MainWindow
        {
            DataContext = windowDataContext,
        };
        desktop.MainWindow = mainWindow;

        // Initialize session + load existing sessions.
        // For first run (onboarding), session is created AFTER wizard completes
        // (in ShowOnboardingThenMain) so it uses the wizard's provider/model.
        // For subsequent runs, session is created here with config from disk.
        _ = Task.Run(async () =>
        {
            try
            {
                var sessionManager = Services.GetRequiredService<SessionManager>();
                await sessionManager.EnsureDefaultSessionAsync().ConfigureAwait(false);

                // Load existing sessions into the sidebar.
                var sessionsVm = mainViewModel.Sessions;
                sessionsVm.RefreshCommand.Execute(null);
            }
            catch (Exception ex)
            {
                Services.GetService<ILogger<App>>()?.LogError(ex, "Session initialization failed");
            }
        });
    }

    /// <summary>
    ///     Shutdown handler — stop the DI host with a 5-second grace period so
    ///     the agent loop / IPC pipe / session store can flush. Without this,
    ///     background Task.Run instances keep the process alive after the main
    ///     window closes (the "window won't close" hang).
    /// </summary>
    private static async void OnShutdownRequested(object? sender, ShutdownRequestedEventArgs e)
    {
        if (Host is null) return;
        try
        {
            // Cancel = false lets the host run its IHostedService.StopAsync
            // sequence (which disposes the agent loop, flushes sessions, etc.).
            // The 5s grace period matches the default IHost.ApplicationLifetime
            // shutdown timeout.
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await Host.StopAsync(cts.Token).ConfigureAwait(false);
            Host.Dispose();
        }
        catch (Exception ex)
        {
            // Logging may already be disposed — fall back to stderr.
            Console.Error.WriteLine($"Harbor shutdown error: {ex}");
        }
    }
}
