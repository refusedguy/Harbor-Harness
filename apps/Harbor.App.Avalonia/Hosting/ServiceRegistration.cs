using Harbor.Abstractions.Agents;
using Harbor.Abstractions.Events;
using Harbor.Abstractions.Permissions;
using Harbor.Abstractions.Providers;
using Harbor.Abstractions.Sessions;
using Harbor.Abstractions.Tools;
using Harbor.App.Avalonia.Services;
using Harbor.Application.Agents;
using Harbor.Application.Permissions;
using Harbor.Application.Resilience;
using Harbor.Application.Sessions;
using Harbor.Core.Tools;
using Harbor.Ipc.Client;
using Harbor.Ipc.InProcess;
using Harbor.Ui.Framework.Navigation;
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
///     <c>Harbor.Cli.Hosting.HostBuilder.RegisterCore</c> for the core Harbor
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
        services.AddSingleton<DialogService>();
        services.AddSingleton<IDialogService>(sp => sp.GetRequiredService<DialogService>());
        services.AddSingleton<AvaloniaFilePicker>();
        services.AddSingleton<IFilePicker>(sp => sp.GetRequiredService<AvaloniaFilePicker>());
        services.AddSingleton<SessionManager>();
        services.AddSingleton<ISessionManager>(sp => sp.GetRequiredService<SessionManager>());
        services.AddSingleton<GitService>();
        services.AddSingleton<ToastService>();
        services.AddSingleton<IToastService>(sp => sp.GetRequiredService<ToastService>());
        services.AddSingleton<OverlayStackService>();
        services.AddSingleton<IOverlayStack>(sp => sp.GetRequiredService<OverlayStackService>());
        services.AddSingleton<WindowChromeService>();
        services.AddSingleton<KeyboardShortcutService>();
        services.AddSingleton<IShellChrome, AvaloniaShellChrome>();
        services.AddSingleton<IWorkspaceCommands, AvaloniaWorkspaceCommands>();
        services.AddSingleton<DefaultUiProjector>();
        services.AddSingleton<AvaloniaUiViewport>();
        services.AddSingleton<ChatStreamingPresenter>();
        services.AddSingleton<UiRenderEngine>();
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
