using Harbor.App.Avalonia.Services;
using Harbor.App.Avalonia.ViewModels;
using Harbor.Ui.Framework.Navigation;
using Microsoft.Extensions.DependencyInjection;

namespace Harbor.E2E.App.Avalonia;

/// <summary>
///     The manual clock the toast auto-dismiss delay reads in the headless
///     Avalonia E2E suite.
/// </summary>
/// <remarks>
///     <para>
///         <b>What this replaces.</b> The shell's toast auto-dismiss used to
///         be <c>await Task.Delay(TimeSpan.FromSeconds(4))</c>, a PRODUCT
///         timer inside the code under test. Every test that watched a toast
///         leave paid four real seconds, and no test-side change could
///         shorten it: the wait was in the code under test.
///     </para>
///     <para>
///         <b>The seam.</b> <see cref="MainViewModel" /> now takes a
///         <see cref="TimeProvider" /> (production default:
///         <see cref="TimeProvider.System" />) and the delay reads it. This
///         class supplies a <see cref="ManualTimeProvider" /> instead, so
///         "four seconds later" is an instant the test steps to.
///     </para>
///     <para>
///         <b>Scope.</b> The substitution is deliberately narrow: only
///         <see cref="MainViewModel" /> is rebuilt with the manual clock.
///         The container's own <see cref="TimeProvider" /> registration stays
///         the system clock, because <c>RetryPolicy</c> and <c>AgentLoop</c>
///         consume it. A retry back-off or an update-flush timer read from a
///         clock that never moves would hang, not slow down.
///     </para>
/// </remarks>
public static class ToastClock
{
    /// <summary>
    ///     The process-wide manual clock. One Avalonia application (and one
    ///     DI host) exists per test process, so the clock that the shell reads
    ///     is the same instance every test in the process advances.
    /// </summary>
    public static readonly ManualTimeProvider Instance = new(DateTimeOffset.UnixEpoch);

    /// <summary>Move the virtual clock forward.</summary>
    public static void Advance(TimeSpan delta) => Instance.Advance(delta);

    /// <summary>
    ///     Move the virtual clock past the production toast deadline, so every
    ///     toast armed at the current instant is dismissed.
    /// </summary>
    public static void AdvancePastToastDeadline() => Instance.Advance(MainViewModel.ToastAutoDismissDelay);

    /// <summary>
    ///     Replace the <see cref="MainViewModel" /> registration with one that
    ///     reads <see cref="Instance" /> instead of the system clock.
    /// </summary>
    /// <param name="services">The container's service collection, before the
    ///     provider is built.</param>
    public static void ApplyTo(IServiceCollection services)
    {
        // The constructor is spelled out rather than delegated to
        // ActivatorUtilities: the explicit form cannot silently bind the
        // clock to a different parameter, and a change to the constructor
        // breaks the build here instead of at run time.
        services.AddSingleton<MainViewModel>(sp => new MainViewModel(
            sp.GetRequiredService<IContentHost>(),
            sp.GetRequiredService<ShellInfrastructure>(),
            sp.GetRequiredService<CommandPaletteViewModel>(),
            sp.GetRequiredService<ProjectFileTreeScanner>(),
            sp.GetService<IOverlayStack>(),
            Instance));
    }
}
