using Harbor.App.Avalonia.Navigation;
using Harbor.App.Avalonia.ViewModels;
using Harbor.App.Avalonia.ViewModels.Board;
using Harbor.Desktop.Shared.Locators;
using Harbor.Desktop.Abstractions.ViewModels;
using Harbor.Ui.Framework.Animation;
using Harbor.Ui.Framework.Navigation;
using Harbor.Ui.Framework.Overlays;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using CommunityToolkit.Mvvm.Messaging;
namespace Harbor.App.Avalonia.Hosting;
/// <summary>
///     View-model registration — long-lived shell VMs are Singletons so
///     that resolves are stable across the app lifetime. Transient
///     resolution of MainViewModel was a DeepSeek-flagged bug:
///     CommandPaletteViewModel has a direct constructor dependency on
///     MainViewModel (via DI), and a transient MainViewModel meant each
///     invocation got a fresh shell with no bound ChatViewModel/Sessions/etc.
///     The MainViewModel↔CommandPaletteViewModel cycle is broken via
///     Lazy&lt;CommandPaletteViewModel&gt; in MainViewModel.
/// </summary>
/// <remarks>
///     <para>
///         The shell VMs (Main/Chat/SessionList/CommandPalette) form a
///         singleton cluster — they reference each other and share the
///         UiStore subscription.
///     </para>
///     <para>
///         Edit-style VMs (CodeEditor/Diff/TokenUsage) stay Transient
///         because they hold per-document state that the user may want to
///         discard on close. TokenUsageViewModel is the exception — it's
///         a singleton because MainViewModel pushes RecordUsage calls on
///         every UiStore transition and SessionManager resolves the same
///         instance to call Clear() on session switch.
///     </para>
///     <para>
///         <see cref="IContentHost"/> is the renderer-agnostic contract from
///         <see cref="Harbor.Ui.Framework.Navigation"/>. The Avalonia
///         implementation (<see cref="AvaloniaContentHost"/>) aggregates the
///         shell view-models so <see cref="MainViewModel"/> no longer needs
///         11 individual constructor parameters.
///     </para>
/// </remarks>
internal static class ViewModelRegistration
{
    /// <summary>
    ///     Register every view-model on the DI container with the
    ///     appropriate lifetime (singleton for shell VMs, transient for
    ///     edit-style VMs). Also registers the centralized
    ///     <see cref="Harbor.Desktop.Shared.Locators.IViewModelLocator" />
    ///     so code-behind, dialogs and shell VMs resolve through it instead
    ///     of scattered <c>App.Services.GetService&lt;&gt;()</c> calls.
    /// </summary>
    /// <param name="services">The DI container.</param>
    public static void Register(IServiceCollection services)
    {
        // One-time locator convention — view-models + overlays + dialogs
        // resolve through it. No per-call-site AddSingleton<T> spread.
        services.AddViewModelLocator();

        // Shared shell state (ObservableValidator) — single instance
        // so MainViewModel keeps it updated.
        services.AddSingleton<ShellStatus>();

        services.AddSingleton<OverlayController>();
        services.AddSingleton<CostAnimator>();

        services.AddSingleton<IContentHost, AvaloniaContentHost>();
        services.AddSingleton<AvaloniaContentHost>();

        services.AddSingleton(sp => new ShellInfrastructure(
            sp.GetRequiredService<IDispatcherAdapter>(),
            sp.GetRequiredService<ILogger<MainViewModel>>(),
            sp.GetRequiredService<IThemeService>(),
            sp.GetRequiredService<IToastService>(),
            sp.GetRequiredService<TuiEffectHost>(),
            sp.GetRequiredService<OverlayController>(),
            sp.GetRequiredService<CostAnimator>(),
            sp.GetRequiredService<IMessenger>(),
            sp.GetRequiredService<ShellStatus>()));

        // ContentHost aggregates shell VMs so MainViewModel only needs
        // one navigation dependency instead of 11 individual view-model
        // parameters. Register the host BEFORE MainViewModel so DI can
        // resolve its constructor.
        // ShellInfrastructure groups the remaining shell services.
        services.AddSingleton<MainViewModel>();
        services.AddSingleton<ChatViewModel>();
        services.AddSingleton<SessionListViewModel>();
        services.AddSingleton<CommandPaletteViewModel>();
        services.AddSingleton<BoardViewModel>();

        services.AddSingleton<Harbor.Desktop.Abstractions.ViewModels.ProviderBrowserViewModel>();
        services.AddSingleton<Harbor.App.Avalonia.ViewModels.ProviderModelPickerViewModel>();
        services.AddSingleton<SettingsViewModel>();
        services.AddSingleton<CodeEditorViewModel>();
        services.AddSingleton<Harbor.Desktop.Abstractions.ViewModels.DiffViewModel>();

        services.AddSingleton<TokenUsageViewModel>();

        services.AddTransient<Harbor.App.Avalonia.ViewModels.OnboardingViewModel>();

        services.AddSingleton<Harbor.App.Avalonia.ViewModels.FocusSessionViewModel>();
    }
}
