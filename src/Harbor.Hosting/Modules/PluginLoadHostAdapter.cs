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
///     Thread-safety is provided by the underlying registries (<c>ConcurrentDictionary</c>
///     -backed). The <c>TuiPlugins</c> list uses a lock since
///     <see cref="List{T} " /> is not thread-safe. Panel providers are forwarded
///     directly into the host-owned <see cref="PanelRegistry" /> singleton, which is
///     itself thread-safe.
/// </remarks>
internal sealed class PluginLoadHost : IPluginLoadHost
{
    private readonly IAgentRegistry _agents;
    private readonly IProviderRegistry _providers;
    private readonly IToolRegistry _tools;
    private readonly object _tuiLock = new();
    private readonly List<ITuiPlugin> _tuiPlugins = new();

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

    /// <summary>
    ///     The TUI plugins collected via <see cref="RegisterTuiPlugin" />. The renderer
    ///     reads this list after construction and calls
    ///     <see cref="ITuiPlugin.RegisterTui" /> for each entry.
    /// </summary>
    public IReadOnlyList<ITuiPlugin> TuiPlugins
    {
        get
        {
            lock (_tuiLock)
            {
                return _tuiPlugins.ToArray();
            }
        }
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

    /// <inheritdoc />
    public Result RegisterTuiPlugin(ITuiPlugin plugin)
    {
        lock (_tuiLock)
        {
            _tuiPlugins.Add(plugin);
        }
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
