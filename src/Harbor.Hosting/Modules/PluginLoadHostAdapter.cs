#if HARBOR_WITH_PLUGINS
using System.Collections.Concurrent;
using CSharpFunctionalExtensions;
using Harbor.Abstractions.Agents;
using Harbor.Abstractions.Events;
using Harbor.Abstractions.Models.Identifiers;
using Harbor.Abstractions.Providers;
using Harbor.Abstractions.Tools;
using Harbor.Plugins.Abstractions;
using Harbor.Terminal.Abstractions.Plugins;
using Harbor.Ui.Framework.Panels;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
namespace Harbor.Hosting;
/// <summary>
///     Adapter that exposes the already-constructed <c>ToolRegistry</c>,
///     <c>ProviderRegistry</c>, and <c>AgentRegistry</c> instances (plus the host's
///     <see cref="IServiceCollection" />, <see cref="IConfiguration" />,
///     <see cref="ILoggerFactory" />, and <see cref="IEventBus" />) to the plugin
///     runtime (<see cref="Harbor.Plugins.Hosting.PluginHost" />) by implementing
///     <see cref="IPluginLoadHost" />.
/// </summary>
/// <remarks>
///     Thread-safety is provided by the underlying registries
///     (<c>ConcurrentDictionary</c>-backed), and by the host-owned
///     <see cref="PanelRegistry" /> singleton, which is itself thread-safe. Nothing
///     here is guarded by a lock: <see cref="RegisterTuiPlugin" /> stores nothing
///     (#916).
/// </remarks>
internal sealed class PluginLoadHost : IPluginLoadHost
{
    private readonly IAgentRegistry _agents;
    private readonly IProviderRegistry _providers;
    private readonly IToolRegistry _tools;

    /// <summary>
    ///     Session-store backends contributed via <see cref="RegisterSessionStore" />,
    ///     read by <c>StorageModule</c> when it builds the storage registry.
    /// </summary>
    public ConcurrentDictionary<string, Func<ISessionStore>> SessionStores { get; } =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    ///     TUI backends contributed via <see cref="RegisterTuiBackend" />, read by
    ///     <c>TuiModule</c> when it builds the backend registry — which is the same
    ///     registry the runtime-swap table is derived from, so a plugin backend is
    ///     listed by <c>/renderer</c> and can be swapped to without a second list.
    /// </summary>
    public ConcurrentDictionary<string, PluginTuiBackend> TuiBackends { get; } =
        new(StringComparer.OrdinalIgnoreCase);

    public PluginLoadHost(
        IServiceCollection services,
        IConfiguration configuration,
        ILoggerFactory loggerFactory,
        IEventBus eventBus,
        IToolRegistry tools,
        IProviderRegistry providers,
        IAgentRegistry agents,
        PanelRegistry panels)
    {
        Services = services;
        Configuration = configuration;
        LoggerFactory = loggerFactory;
        EventBus = eventBus;
        _tools = tools;
        _providers = providers;
        _agents = agents;
        Panels = panels ?? throw new ArgumentNullException(nameof(panels));
    }

    /// <summary>
    ///     The host-owned <see cref="PanelRegistry" /> singleton. Plugin-contributed
    ///     <see cref="IPanelProvider" />s land here via
    ///     <see cref="RegisterPanelProvider" />; the active interactive renderer
    ///     reads from this same instance (resolved from DI).
    /// </summary>
    public PanelRegistry Panels
    {
        get;
    }

    /// <inheritdoc />
    public IServiceCollection Services
    {
        get;
    }

    /// <inheritdoc />
    public IConfiguration Configuration
    {
        get;
    }

    /// <inheritdoc />
    public ILoggerFactory LoggerFactory
    {
        get;
    }

    /// <inheritdoc />
    public IEventBus EventBus
    {
        get;
    }

    /// <inheritdoc />
    public Result RegisterTool(ITool tool) => _tools.Register(tool);

    /// <inheritdoc />
    public Result RegisterProvider(ProviderId providerId, Func<ILlmClient> factory)
    {
        _providers.Register(providerId, factory);
        return Result.Success();
    }

    /// <inheritdoc />
    public Result RegisterAgent(AgentDefinition agent) => _agents.Register(agent);

    /// <summary>
    ///     Accept a TUI plugin contributed by a CS plugin, and keep nothing.
    /// </summary>
    /// <remarks>
    ///     <b>Stores nothing (#916).</b> This used to append to a
    ///     <c>TuiPlugins</c> list on this class; the list had zero readers
    ///     anywhere in the repository, and the member was never on
    ///     <see cref="IPluginLoadHost" /> — it was born on this
    ///     <c>internal sealed class</c> in <c>ce522a9a</c> — so the seven documents
    ///     that named <c>IPluginLoadHost.TuiPlugins</c> were naming a member that has
    ///     never existed on that interface. Deleting the list makes those sentences
    ///     true by construction and removes the only writable copy of the door's
    ///     payload.
    ///     <para>
    ///         The door itself stays, and deliberately so:
    ///         <c>ITuiPlugin</c> is a <b>closed seam (#564)</b> — a marker plus a door
    ///         is what <c>ExtensionAxisFreezeRule</c> grades, and removing the door
    ///         would fail <c>EverySealedAxis_IsDispatchedAndOpened</c> and quietly
    ///         reopen the freeze question rather than settle it. So a TUI plugin still
    ///         loads, is still accepted without error, and still paints nothing:
    ///         nothing in the product calls <see cref="ITuiPlugin.RegisterTui" />.
    ///     </para>
    ///     <para>
    ///         What a future change that does wire a renderer has to add is a field
    ///         to collect into and the reader that enumerates it. The guard that makes
    ///         that a deliberate edit rather than an accident is
    ///         <c>tests/Harbor.Architecture.Tests/CellForgeWidgetAxisRules.cs</c>:
    ///         <c>ViewSeam_DeclaredStatusMatchesWhatTheProductRenders</c> is two-sided on
    ///         the consumer, and <c>ViewSeam_HasNoFirstImplementorInProductSource</c>
    ///         (#916) turns red the moment a type implements the marker.
    ///     </para>
    /// </remarks>
    public Result RegisterTuiPlugin(ITuiPlugin plugin)
    {
        ArgumentNullException.ThrowIfNull(plugin);
        return Result.Success();
    }

    /// <inheritdoc />
    public Result RegisterPanelProvider(IPanelProvider panel) => Panels.Register(panel);

    /// <inheritdoc />
    public Result RegisterSessionStore(string backendId, Func<ISessionStore> factory)
    {
        if (string.IsNullOrWhiteSpace(backendId))
        {
            return Result.Failure("Session-store backend id must be a non-empty string.");
        }

        if (factory is null)
        {
            return Result.Failure($"Session-store backend '{backendId}' has no factory.");
        }

        string id = backendId.Trim().ToLowerInvariant();
        if (!SessionStores.TryAdd(id, factory))
        {
            return Result.Failure($"Session-store backend '{id}' is already registered; skipping duplicate.");
        }

        return Result.Success();
    }

    /// <inheritdoc />
    public Result RegisterTuiBackend(string backendId, IReadOnlyList<string>? aliases, Func<ITuiRenderer> factory)
    {
        if (string.IsNullOrWhiteSpace(backendId))
        {
            return Result.Failure("TUI backend id must be a non-empty string.");
        }

        if (factory is null)
        {
            return Result.Failure($"TUI backend '{backendId}' has no factory.");
        }

        string id = backendId.Trim().ToLowerInvariant();
        if (!TuiBackends.TryAdd(id, new PluginTuiBackend(aliases, factory)))
        {
            return Result.Failure($"TUI backend '{id}' is already registered; skipping duplicate.");
        }

        return Result.Success();
    }
}
#endif
// HARBOR_MINIMAL: PluginLoadHost is omitted — the entire Harbor.Plugins.*
// stack is excluded from the project reference graph in minimal builds, so
// there's no IPluginLoadHost to implement. HostBuilder.cs gates the
// construction of this type behind `#if !HARBOR_MINIMAL` accordingly.
