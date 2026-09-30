using Harbor.Abstractions.Agents;
using Harbor.Abstractions.Events;
using Harbor.Abstractions.Filesystem;
using Harbor.Abstractions.Git;
using Harbor.Abstractions.Permissions;
using Harbor.Abstractions.Providers;
using Harbor.Abstractions.Sessions;
using Harbor.Abstractions.Terminal;
using Harbor.Abstractions.Tools;
using Harbor.App.Avalonia.Services;
using Harbor.App.Avalonia.ViewModels.Terminal;
using Harbor.Application.Agents;
using Harbor.Application.Filesystem;
using Harbor.Application.Git;
using Harbor.Application.Permissions;
using Harbor.Application.Resilience;
using Harbor.Application.Sessions;
using Harbor.Registries.Tools;
using Harbor.Ipc.Client;
using Harbor.Ipc.InProcess;
using Harbor.Terminal.Pty;
using Harbor.Ui.Framework.Navigation;
using Harbor.Ui.Framework.Forking;
using Harbor.Ui.Framework.Overlays;
using CommunityToolkit.Mvvm.Messaging;
using Harbor.Ui.Framework.Projection;
using Harbor.Ui.Framework.Rendering;
using Harbor.Ui.Framework.Sessions;
using Harbor.Ui.Framework.State;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
namespace Harbor.App.Avalonia.Hosting;
/// <summary>
///     Core + app-local service registration. Mirrors
///     <c>Harbor.App.Cli.Hosting.HostBuilder.RegisterCore</c> for the core Harbor
///     services, plus the Avalonia-specific shell services (ThemeService,
///     SessionManager, ChatStreamingPresenter, etc.) and the IHarborClient
///     in-process / ipc-client wiring.
/// </summary>
/// <remarks>
///     <para>
///         Methods are intentionally ordered to be called in dependency order
///         by <c>AppHost.BuildAsync</c>:
///     </para>
///     <list type="number">
///         <item><see cref="Register" /> — core services (no dependencies).</item>
///         <item><see cref="RegisterCompactionAndPermissions" /> — depends on the eager registries.</item>
///         <item>
///             <see cref="RegisterEagerRegistries" /> — registers the already-built ToolRegistry / ProviderRegistry /
///             AgentRegistry / McpRegistry.
///         </item>
///         <item><see cref="RegisterAppServices" /> — app-local singletons (ThemeService, SessionManager, etc.).</item>
///         <item><see cref="RegisterHarborClient" /> — IHarborClient based on HARBOR_MODE env.</item>
///     </list>
/// </remarks>
internal static class ServiceRegistration
{
    /// <summary>
    ///     Register the Avalonia-specific app-local singletons (shell
    ///     services, session-management cluster, presentation helpers,
    ///     and the <see cref="AvaloniaDispatcherAdapter" /> that bridges
    ///     UiStore → UI-thread). The adapter is bound to the UiStore
    ///     AFTER the host is built (in <c>AppHost.BuildAsync</c>) so VMs
    ///     that resolve the adapter can subscribe to OnUiThread without
    ///     racing with a Bind call from another VM's constructor.
    ///     Thread-affinity contract ([G9] #196): every singleton below is
    ///     UI-thread-affine. Concurrent background producers must go through
    ///     <c>UiStore</c> / <c>IDispatcherAdapter</c>, not through direct
    ///     member access — see <c>ViewModelRegistration</c> for the VM side.
    /// </summary>
    /// <param name="services">The DI container.</param>
    public static void RegisterAppServices(IServiceCollection services)
    {
        // Harbor TUI TEA store + effect host — the single source of truth for the chat UI.
        services.AddSingleton<UiStore>();
        services.AddSingleton<TuiEffectHost>(sp =>
        {
            var agent = sp.GetRequiredService<IAgentRunner>();
            var store = sp.GetRequiredService<UiStore>();
            var logger = sp.GetRequiredService<ILogger<TuiEffectHost>>();
            return new TuiEffectHost(agent, store, null, default, logger);
        });

        services.AddSingleton<IMessenger, WeakReferenceMessenger>();
        services.AddSingleton<ThemeService>();
        services.AddSingleton<IThemeService>(sp => sp.GetRequiredService<ThemeService>());
        // Role aliases over the one ThemeService singleton (#469): consumers
        // depend on the narrowest role they actually use (ISP).
        services.AddSingleton<IThemeReader>(sp => sp.GetRequiredService<ThemeService>());
        services.AddSingleton<IThemeApplier>(sp => sp.GetRequiredService<ThemeService>());
        services.AddSingleton<IThemeWatcher>(sp => sp.GetRequiredService<ThemeService>());
        services.AddSingleton<DialogService>();
        services.AddSingleton<IDialogService>(sp => sp.GetRequiredService<DialogService>());
        services.AddSingleton<AvaloniaFilePicker>();
        services.AddSingleton<IFilePicker>(sp => sp.GetRequiredService<AvaloniaFilePicker>());
        services.AddSingleton<SessionEventRouter>();
        services.AddSingleton<SessionStatusService>();
        services.AddSingleton<SessionOptionalFactories>(sp => new SessionOptionalFactories(
            () => sp.GetService<GitService>(),
            () => sp.GetService<IApprovalCoordinator>(),
            () => sp.GetService<TokenUsageViewModel>()?.Clear()));
        services.AddSingleton<SessionLifecycleService>();
        services.AddSingleton<SessionManager>();
        services.AddSingleton<ISessionManager>(sp => sp.GetRequiredService<SessionManager>());
        services.AddSingleton<ISessionQueries>(sp => sp.GetRequiredService<SessionManager>());
        services.AddSingleton<ISessionLifecycle>(sp => sp.GetRequiredService<SessionManager>());
        services.AddSingleton<ISessionStatusTracker>(sp => sp.GetRequiredService<SessionManager>());
        // #470: PanelServices.FromContainer resolves IPanelSessionGateway (not
        // ISessionManager) because the panel contract lives in a lower layer —
        // publish the same instance under the narrow name so the panel bag is
        // actually filled instead of silently degrading.
        services.AddSingleton<Harbor.Ui.Framework.Panels.IPanelSessionGateway>(sp => sp.GetRequiredService<SessionManager>());
        // #537: the Presentation side (GitService) no longer forks `git`. The
        // read-only query contract is Domain; the process spawn is Application.
        services.AddSingleton<IGitQuery, ProcessGitQuery>();
        services.AddSingleton<GitService>();
        services.AddSingleton<ToastService>();
        services.AddSingleton<IToastService>(sp => sp.GetRequiredService<ToastService>());
        services.AddSingleton<OverlayStackService>();
        services.AddSingleton<IOverlayStack>(sp => sp.GetRequiredService<OverlayStackService>());
        services.AddSingleton<WindowChromeService>();
        services.AddSingleton<KeyboardShortcutService>();
        // #672: the floating terminal pane no longer forks a shell itself. The
        // launch goes through ITerminalPaneLauncher, registered here against the
        // one implementation that consults PermissionRuleset first and refuses
        // unless the launch is permitted. THE COMPOSITION ROOT IS THE ONLY PLACE
        // THAT MAY CHOOSE THE RULESET — which is the point: the policy is now one
        // greppable line here instead of a side effect buried in a ViewModel
        // constructor.
        //
        // PermissionRuleset.Default carries no `terminal` rule, and an unmatched
        // permission evaluates to Ask, which this launcher treats as a refusal. So
        // the pane is DENIED by default and an operator opts in explicitly with
        // {"terminal": {"*": "allow"}}. That is deliberate and is the safe
        // direction: this launches an interactive shell with the full inherited
        // environment, and until #672 it launched with no gate at all.
        services.AddSingleton<ITerminalPaneLauncher>(sp => new PermissionGatedTerminalPaneLauncher(
            PermissionRuleset.Default));
        services.AddSingleton<IFloatingTerminals>(sp => new FloatingTerminalService(
            sp.GetRequiredService<IDispatcherAdapter>(),
            () => new FloatingTerminalViewModel(
                sp.GetRequiredService<IDispatcherAdapter>(),
                sp.GetRequiredService<ILogger<FloatingTerminalViewModel>>(),
                cwd => new TerminalPaneViewModel(
                    sp.GetRequiredService<IDispatcherAdapter>(),
                    sp.GetRequiredService<ILogger<TerminalPaneViewModel>>(),
                    sp.GetRequiredService<ITerminalPaneLauncher>(),
                    cwd)),
            sp.GetRequiredService<ILogger<FloatingTerminalService>>()));
        services.AddSingleton<IShellChrome, AvaloniaShellChrome>();
        services.AddSingleton<IWorkspaceCommands, AvaloniaWorkspaceCommands>();
        // #492: the desktop file tree stopped walking the filesystem. These three
        // registrations are the whole of the fix on this side: the walk is the
        // Domain `IDirectoryLister` (implemented in Harbor.Application by
        // SystemDirectoryLister, which #667 already added for the TUI sidebar), the
        // ignore list and the extension→icon map are the Domain `IFileTreePolicy`,
        // and the recursion with its depth and node budgets is
        // `ProjectFileTreeScanner` — the only one of the three that is app-local,
        // because the TUI sidebar is lazy and per-directory while this tree is
        // eager and depth-capped. The app does not implement either port: see
        // AvaloniaFileTreeWalkRules, which fails the build if it ever does.
        services.AddSingleton<IDirectoryLister, SystemDirectoryLister>();
        services.AddSingleton<IFileTreePolicy, DefaultFileTreePolicy>();
        services.AddSingleton<ProjectFileTreeScanner>();
        // #569: AvaloniaWorkspaceCommands takes an ILogger<AvaloniaWorkspaceCommands>
        // so its sync IWorkspaceCommands members can report a fault instead of
        // dropping the Task (a void member whose body is ExecuteAsync has no
        // '_ =' marker and so was invisible to the original audit).
        services.AddSingleton<DefaultUiProjector>();
        services.AddSingleton<AvaloniaUiViewport>();
        services.AddSingleton<ChatStreamingPresenter>();
        services.AddSingleton<UiRenderEngine>();
        // #670: SessionFactory forks through the core's SessionForkService, which lives in
        // Harbor.Application — a layer this Presentation framework may not reference. The
        // adapter below is the bridge, same shape as CommonConfigReaderAdapter (#453, ADR-009).
        services.AddSingleton<ISessionForker, SessionForkerAdapter>();
        services.AddSingleton<SessionFactory>();
        services.AddSingleton<SessionSwitcher>();
        services.AddSingleton<SessionGitTracker>();
        services.AddSingleton<IChatViewBinder, AvaloniaChatViewBinder>();
        services.AddSingleton<SessionStatusTracker>();
        services.AddSingleton<IDispatcherAdapter, AvaloniaDispatcherAdapter>();
    }

    /// <summary>
    ///     Register <see cref="IHarborClient" /> — in-process by default, or
    ///     IPC client when <c>HARBOR_MODE=ipc-client</c>. The IPC pipe name
    ///     is overridable via <c>HARBOR_IPC_PIPE</c> (default
    ///     <c>harbor-ipc</c>). Mirrors the CLI's
    ///     <c>HostBuilder.RegisterIpcMode</c> dispatch.
    /// </summary>
    /// <param name="services">The DI container.</param>
    public static void RegisterHarborClient(IServiceCollection services)
    {
        string ipcMode = Environment.GetEnvironmentVariable("HARBOR_MODE") ?? "inprocess";
        string ipcPipe = Environment.GetEnvironmentVariable("HARBOR_IPC_PIPE") ?? "harbor-ipc";
        switch (ipcMode.ToLowerInvariant())
        {
            case "ipc-client":
                services.UseIpcHarborClient(ipcPipe);
                break;
            default:
                services.UseInProcessHarborClient();
                break;
        }
    }
}
