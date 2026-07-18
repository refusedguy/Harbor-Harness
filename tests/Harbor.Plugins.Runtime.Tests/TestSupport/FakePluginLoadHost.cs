using System.Collections.Concurrent;
using CSharpFunctionalExtensions;
using Harbor.Abstractions.Agents;
using Harbor.Abstractions.Events;
using Harbor.Abstractions.Models.Identifiers;
using Harbor.Abstractions.Providers;
using Harbor.Abstractions.Tools;
using Harbor.Tui.Abstractions.Panels;
using Harbor.Tui.Abstractions.Plugins;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
namespace Harbor.Plugins.Runtime.Tests.TestSupport;

/// <summary>
///     In-memory <see cref="IPluginLoadHost" /> for tests. Captures all Register* calls
///     so tests can assert on them. Thread-safe via ConcurrentDictionary / locks.
/// </summary>
public sealed class FakePluginLoadHost : IPluginLoadHost
{
    private readonly ConcurrentDictionary<string, ITool> _tools = new();
    private readonly ConcurrentDictionary<ProviderId, Func<ILlmClient>> _providers = new();
    private readonly ConcurrentDictionary<AgentName, AgentDefinition> _agents = new();
    private readonly List<ITuiPlugin> _tuiPlugins = new();
    private readonly List<IPanelProvider> _panelProviders = new();
    private readonly IEventBus _eventBus = new InMemoryEventBus(NullLogger<InMemoryEventBus>.Instance);

    /// <summary>Initialize the fake host with empty registries.</summary>
    public FakePluginLoadHost()
    {
        Services = new ServiceCollection();
        Configuration = new ConfigurationBuilder().Build();
        LoggerFactory = NullLoggerFactory.Instance;
    }

    /// <inheritdoc />
    public IServiceCollection Services { get; }

    /// <inheritdoc />
    public IConfiguration Configuration { get; }

    /// <inheritdoc />
    public ILoggerFactory LoggerFactory { get; }

    /// <inheritdoc />
    public IEventBus EventBus => _eventBus;

    /// <summary>All tools registered via <see cref="RegisterTool" />.</summary>
    public IReadOnlyList<ITool> RegisteredTools => _tools.Values.ToArray();

    /// <summary>All provider ids registered via <see cref="RegisterProvider" />.</summary>
    public IReadOnlyList<ProviderId> RegisteredProviderIds => _providers.Keys.ToArray();

    /// <summary>All agents registered via <see cref="RegisterAgent" />.</summary>
    public IReadOnlyList<AgentDefinition> RegisteredAgents => _agents.Values.ToArray();

    /// <summary>All TUI plugins registered via <see cref="RegisterTuiPlugin" />.</summary>
    public IReadOnlyList<ITuiPlugin> RegisteredTuiPlugins
    {
        get { lock (_tuiPlugins) { return _tuiPlugins.ToArray(); } }
    }

    /// <summary>All panel providers registered via <see cref="RegisterPanelProvider" />.</summary>
    public IReadOnlyList<IPanelProvider> RegisteredPanelProviders
    {
        get { lock (_panelProviders) { return _panelProviders.ToArray(); } }
    }

    /// <inheritdoc />
    public Result RegisterTool(ITool tool)
    {
        return _tools.TryAdd(tool.Name.Value, tool)
            ? Result.Success()
            : Result.Failure($"Tool '{tool.Name}' already registered.");
    }

    /// <inheritdoc />
    public Result RegisterProvider(ProviderId providerId, Func<ILlmClient> factory)
    {
        _providers[providerId] = factory;
        return Result.Success();
    }

    /// <inheritdoc />
    public Result RegisterAgent(AgentDefinition agent)
    {
        return _agents.TryAdd(agent.Name, agent)
            ? Result.Success()
            : Result.Failure($"Agent '{agent.Name}' already registered.");
    }

    /// <inheritdoc />
    public Result RegisterTuiPlugin(ITuiPlugin plugin)
    {
        lock (_tuiPlugins) { _tuiPlugins.Add(plugin); }
        return Result.Success();
    }

    /// <inheritdoc />
    public Result RegisterPanelProvider(IPanelProvider panel)
    {
        lock (_panelProviders) { _panelProviders.Add(panel); }
        return Result.Success();
    }
}
