#if !HARBOR_MINIMAL
using Harbor.Plugins.Abstractions;
using CSharpFunctionalExtensions;
using Harbor.Abstractions.Agents;
using Harbor.Abstractions.Events;
using Harbor.Abstractions.Models.Identifiers;
using Harbor.Abstractions.Providers;
using Harbor.Abstractions.Tools;
using Harbor.Plugins.Runtime;
using Harbor.Ui.Framework.Panels;
using Harbor.Terminal.Abstractions.Plugins;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
namespace Harbor.Cli.Hosting;

/// <summary>
///     Adapter that exposes the already-constructed <c>ToolRegistry</c>,
///     <c>ProviderRegistry</c>, and <c>AgentRegistry</c> instances (plus the host's
///     <see cref="IServiceCollection" />, <see cref="IConfiguration" />,
///     <see cref="ILoggerFactory" />, and <see cref="IEventBus" />) to the
///     <see cref="CsPluginLoader" /> as an <see cref="IPluginLoadHost" />.
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
    private readonly IServiceCollection _services;
    private readonly IConfiguration _configuration;
    private readonly ILoggerFactory _loggerFactory;
    private readonly IEventBus _eventBus;
    private readonly IToolRegistry _tools;
    private readonly IProviderRegistry _providers;
    private readonly IAgentRegistry _agents;
    private readonly PanelRegistry _panels;
    private readonly List<ITuiPlugin> _tuiPlugins = new();
    private readonly object _tuiLock = new();

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
        _services = services;
        _configuration = configuration;
        _loggerFactory = loggerFactory;
        _eventBus = eventBus;
        _tools = tools;
        _providers = providers;
        _agents = agents;
        _panels = panels ?? throw new ArgumentNullException(nameof(panels));
    }

    /// <inheritdoc />
    public IServiceCollection Services => _services;

    /// <inheritdoc />
    public IConfiguration Configuration => _configuration;

    /// <inheritdoc />
    public ILoggerFactory LoggerFactory => _loggerFactory;

    /// <inheritdoc />
    public IEventBus EventBus => _eventBus;

    /// <summary>
    ///     The host-owned <see cref="PanelRegistry" /> singleton. Plugin-contributed
    ///     <see cref="IPanelProvider" />s land here via
    ///     <see cref="RegisterPanelProvider" />; the active interactive renderer
    ///     reads from this same instance (resolved from DI).
    /// </summary>
    public PanelRegistry Panels => _panels;

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
    public Result RegisterPanelProvider(IPanelProvider panel) => _panels.Register(panel);
}
#endif
// HARBOR_MINIMAL: PluginLoadHost is omitted — the entire Harbor.Plugins.*
// stack is excluded from the project reference graph in minimal builds, so
// there's no IPluginLoadHost to implement. HostBuilder.cs gates the
// construction of this type behind `#if !HARBOR_MINIMAL` accordingly.
