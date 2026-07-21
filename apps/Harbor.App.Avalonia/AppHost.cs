using System.Diagnostics;
using Excubo.Analyzers.DependencyInjection;
using Harbor.Abstractions.Agents;
using Harbor.Abstractions.Events;
using Harbor.Abstractions.Permissions;
using Harbor.Abstractions.Providers;
using Harbor.Abstractions.Sessions;
using Harbor.Abstractions.Tools;
using Harbor.App.Avalonia.Configuration;
using Harbor.App.Avalonia.Hosting;
using Harbor.App.Avalonia.Services;
using Harbor.App.Avalonia.ViewModels.Shell;
using Harbor.Core.Sessions;
using Harbor.Desktop.Abstractions.Configuration;
using Harbor.Ipc;
using Harbor.Ui.Framework.State;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
namespace Harbor.App.Avalonia;
/// <summary>
///     Composition root for the standalone Harbor Avalonia app. Mirrors the wiring
///     in <c>Harbor.Cli/Hosting/HostBuilder.cs</c> but trimmed to a desktop-app subset
///     (no plugins, no MCP, no JSON providers — bring-your-own-provider via Settings).
/// </summary>
/// <remarks>
///     <para>
///         This class is a thin orchestrator — every registration is delegated
///         to a dedicated <c>Harbor.App.Avalonia.Hosting.*Registration</c> class
///         so each piece can be unit-tested in isolation. The orchestrator just
///         calls them in dependency order:
///     </para>
///     <list type="number">
///         <item><see cref="LoggingConfiguration" /> — Serilog setup.</item>
///         <item><see cref="ConfigRegistration" /> — load + register CommonConfig / AvaloniaConfig / auth resolver.</item>
///         <item><see cref="StorageRegistration" /> — pick jsonl / memory backend.</item>
///         <item>
///             <see cref="ToolRegistration" /> / <see cref="ProviderRegistration" /> / <see cref="AgentRegistration" />
///             — eager registry construction.
///         </item>
///         <item>
///             <see cref="ServiceRegistration" /> — core services + eager registries + app-local services +
///             IHarborClient.
///         </item>
///         <item><see cref="ViewModelRegistration" /> — all view-models with appropriate lifetimes.</item>
///     </list>
///     <para>
///         Post-<see cref="HostApplicationBuilder.Build" />, the orchestrator also
///         performs the two cross-wirings that need a built service provider:
///         binding <see cref="UiStore" /> → <see cref="AvaloniaDispatcherAdapter" />
///         and subscribing <see cref="IEventBus" /> → <see cref="UiStore" />.
///     </para>
/// </remarks>
internal static class AppHost
{
    /// <summary>
    ///     Build the DI host. Safe to call from Main before the Avalonia lifetime starts.
    /// </summary>
    /// <param name="args">Command-line args (forwarded to <see cref="Host.CreateApplicationBuilder" />).</param>
    /// <returns>A started <see cref="IHost" />. Dispose on shutdown.</returns>
    // [Exposes(typeof(T))] declarations are validated by Excubo.Analyzers.DependencyInjectionValidation
    // (EDI01–EDI04) and exercised at runtime by Harbor.App.Avalonia.Tests/AppHostDiTests.cs.
    [Exposes(typeof(ITokenEstimator))]
    [Exposes(typeof(IEventBus))]
    [Exposes(typeof(ISystemPromptBuilder))]
    [Exposes(typeof(MessageConverter))]
    [Exposes(typeof(IAgentLoop))]
    [Exposes(typeof(IAgent))]
    [Exposes(typeof(ISessionStore))]
    [Exposes(typeof(ICompactionService))]
    [Exposes(typeof(IPermissionService))]
    [Exposes(typeof(IToolRegistry))]
    [Exposes(typeof(IProviderRegistry))]
    [Exposes(typeof(IAgentRegistry))]
    [Exposes(typeof(IMcpRegistry))]
    [Exposes(typeof(UiStore))]
    [Exposes(typeof(TuiEffectHost))]
    [Exposes(typeof(ThemeService))]
    [Exposes(typeof(DialogService))]
    [Exposes(typeof(AvaloniaFilePicker))]
    [Exposes(typeof(SessionManager))]
    [Exposes(typeof(GitService))]
    [Exposes(typeof(ToastService))]
    [Exposes(typeof(AvaloniaDispatcherAdapter))]
    [Exposes(typeof(IHarborClient))]
    [Exposes(typeof(IAppConfigStore<AvaloniaConfig>))]
    [Exposes(typeof(AvaloniaConfig))]
    [Exposes(typeof(ICommonConfigStore))]
    [Exposes(typeof(CommonConfig))]
    [Exposes(typeof(CompositeConfig<AvaloniaConfig>))]
    [Exposes(typeof(OrcaShellViewModel))]
    public static async Task<IHost> BuildAsync(string[] args)
    {
        string homeDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string harborDir = Path.Combine(homeDir, ".harbor");
        string sessionsDir = Path.Combine(harborDir, "sessions");
        Directory.CreateDirectory(harborDir);
        Directory.CreateDirectory(sessionsDir);

        var builder = Host.CreateApplicationBuilder(args);

        // 1. Logging (Serilog) — replaces .NET logging providers.
        LoggingConfiguration.Configure(builder);
        builder.Services.AddHttpClient(); // per-provider HttpClient w/ timeout for OpenAI-compatible clients

        // 2. Bootstrap logger factory — used by eager singletons constructed
        // BEFORE the host is built (config stores, registries, auth resolver).
        var loggerFactory = LoggingConfiguration.CreateBootstrapLoggerFactory();

        // 3. Config — load CommonConfig + AvaloniaConfig eagerly, register
        // stores + auth resolver + composite. Returns the loaded configs.
        var config = await ConfigRegistration.RegisterAsync(builder.Services, loggerFactory, harborDir).ConfigureAwait(false);

        // 4. Storage — pick jsonl/memory based on CommonConfig.StorageBackend (or HARBOR_STORAGE env).
        StorageRegistration.Register(builder.Services, sessionsDir, config.CommonConfig);

        // 5. Registries — build eagerly so the agent can be initialized with them.
        var toolRegistry = ToolRegistration.Build(loggerFactory);
        var providerRegistry = ProviderRegistration.Build(loggerFactory, config.AuthResolver, config.ModelCatalog);
        var agentRegistry = AgentRegistration.Build(config.CommonConfig);

        // 6. Core services + compaction/permissions + eager registries + app-local services + IHarborClient.
        ServiceRegistration.Register(builder.Services);
        ServiceRegistration.RegisterCompactionAndPermissions(builder.Services, providerRegistry, agentRegistry);
        ServiceRegistration.RegisterEagerRegistries(builder.Services, toolRegistry, providerRegistry, agentRegistry, loggerFactory);
        ServiceRegistration.RegisterAppServices(builder.Services);
        ServiceRegistration.RegisterHarborClient(builder.Services);

        // 7. View-models.
        ViewModelRegistration.Register(builder.Services);

        var host = builder.Build();

        // 8. Post-build cross-wirings (need a built service provider).
        // Bind UiStore → AvaloniaDispatcherAdapter exactly once (idempotent).
        // The DI-singleton UiStore is the INITIAL store the dispatcher is
        // bound to; SessionManager.RebindChatViewModel rebinds to per-session
        // stores as the user opens/creates/switches sessions.
        var uiStore = host.Services.GetRequiredService<UiStore>();
        host.Services.GetRequiredService<AvaloniaDispatcherAdapter>().Bind(uiStore);

        // CRITICAL: subscribe to IEventBus and route each agent event to the
        // correct per-session UiStore. Without this routing, a background
        // agent in session A would leak its events into the active session
        // B's chat transcript (the user-visible bug this whole task fixes:
        // "я хочу чтобы агенты не останавливались а я мог их в разных
        // сессиях останавливать работающими").
        //
        // Routing logic:
        //   - AgentStartEvent / CompactionStartedEvent / CompactionCompletedEvent
        //     / SessionStatsEvent carry an explicit SessionId → route directly.
        //   - Other events (TurnStart, MessageUpdate, ToolExecution*, etc.)
        //     don't carry a session id. They are matched to the session id
        //     that the most recent AgentStartEvent declared, tracked in
        //     _currentAgentSessionId. With a singleton IAgent this is correct
        //     because only one PromptAsync can be in flight at a time.
        //   - Fallback: route to the active session's store (or the DI
        //     singleton store if there's no active session yet).
        var sessionManager = host.Services.GetRequiredService<SessionManager>();
        var dispatcherAdapter = host.Services.GetRequiredService<AvaloniaDispatcherAdapter>();
        string? currentAgentSessionId = null;
        var eventBus = host.Services.GetRequiredService<IEventBus>();
        eventBus.Subscribe(async (evt, ct) =>
        {
            try
            {
                string? sessionId = ExtractSessionId(evt, ref currentAgentSessionId);
                UiStore? targetStore = null;
                if (sessionId is not null)
                {
                    targetStore = sessionManager.GetContext(sessionId)?.Store;
                }
                targetStore ??= sessionManager.ActiveContext?.Store
                                ?? dispatcherAdapter.BoundStore
                                ?? uiStore;
                targetStore.Dispatch(evt);
            }
            catch (Exception ex)
            {
                // Defensive: never let a subscriber exception crash the
                // event bus (which would silently drop all subsequent events).
                Debug.WriteLine($"EventBus subscriber crashed: {ex}");
            }
            await Task.CompletedTask;
        });

        // 9. Initialize the agent with a fresh session so the user can start chatting immediately.
        await host.Services.GetRequiredService<SessionManager>().EnsureDefaultSessionAsync().ConfigureAwait(false);

        return host;
    }

    /// <summary>
    ///     Extract the session id from an agent event. For events that
    ///     carry an explicit SessionId (AgentStartEvent, CompactionStartedEvent,
    ///     CompactionCompletedEvent, SessionStatsEvent), returns that id and
    ///     (for AgentStartEvent) updates <paramref name="currentAgentSessionId" />.
    ///     For other events, returns the last-seen AgentStartEvent session id
    ///     so streaming events (MessageUpdate, ToolExecution*, etc.) route to
    ///     the same store as the run they belong to.
    /// </summary>
    /// <param name="evt">The agent event.</param>
    /// <param name="currentAgentSessionId">
    ///     Ref to the tracked current
    ///     running-session id (set by AgentStartEvent).
    /// </param>
    /// <returns>The session id for routing, or null if unknown.</returns>
    private static string? ExtractSessionId(AgentEvent evt, ref string? currentAgentSessionId)
    {
        switch (evt)
        {
            case AgentStartEvent start:
                currentAgentSessionId = start.SessionId;
                return start.SessionId;
            case CompactionStartedEvent cs:
                currentAgentSessionId = cs.SessionId;
                return cs.SessionId;
            case CompactionCompletedEvent cc:
                return cc.SessionId;
            case SessionStatsEvent ss:
                return ss.SessionId;
            case AgentEndEvent:
                // Don't clear currentAgentSessionId — late-arriving events
                // (e.g. a final MessageEnd) still belong to the just-finished run.
                return currentAgentSessionId;
            default:
                return currentAgentSessionId;
        }
    }
}
