using Harbor.App.Avalonia.ViewModels;
using Harbor.App.Avalonia.ViewModels.Board;
using Microsoft.Extensions.DependencyInjection;
namespace Harbor.App.Avalonia.Hosting;
/// <summary>
///     View-model registration — long-lived shell VMs are Singletons so
///     that resolves are stable across the app lifetime. Transient
///     resolution of MainViewModel was a DeepSeek-flagged bug:
///     CommandPaletteViewModel resolves MainViewModel on every command
///     invocation, and a transient MainViewModel meant each invocation got
///     a fresh shell with no bound ChatViewModel/Sessions/etc.
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
/// </remarks>
internal static class ViewModelRegistration
{
    /// <summary>
    ///     Register every view-model on the DI container with the
    ///     appropriate lifetime (singleton for shell VMs, transient for
    ///     edit-style VMs).
    /// </summary>
    /// <param name="services">The DI container.</param>
    public static void Register(IServiceCollection services)
    {
        // Shared shell state (ObservableValidator) — single instance
        // so MainViewModel keeps it updated.
        services.AddSingleton<ShellStatus>();

        // Singleton cluster — shell VMs reference each other.
        services.AddSingleton<MainViewModel>();
        services.AddSingleton<ChatViewModel>();
        services.AddSingleton<SessionListViewModel>();
        services.AddSingleton<CommandPaletteViewModel>();
        services.AddSingleton<BoardViewModel>();

        // Transient — per-document VMs the user may discard on close.
        services.AddTransient<ProviderBrowserViewModel>();
        services.AddTransient<ProviderModelPickerViewModel>();
        services.AddTransient<SettingsViewModel>();
        services.AddTransient<CodeEditorViewModel>();
        services.AddTransient<DiffViewModel>();

        // Singleton: MainViewModel holds the instance and pushes RecordUsage
        // calls on every UiStore transition. SessionManager resolves the
        // same instance to call Clear() on session switch so the chart
        // reflects only the active session's tokens (Task S2 / Problem 3).
        services.AddSingleton<TokenUsageViewModel>();

        // Onboarding VM is transient — created fresh each time the wizard runs
        // (first launch, or re-run from Settings). Holds per-run state (current
        // step, typed API key) that we explicitly want to discard on close.
        services.AddTransient<OnboardingViewModel>();

        services.AddTransient<FocusSessionViewModel>();
    }
}
