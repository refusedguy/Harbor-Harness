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

namespace Harbor.Plugins.Host;

/// <summary>
///     Minimal <see cref="IPluginLoadHost" /> implementation that captures the <see cref="ITool" />
///     instances contributed by CS-source plugins and exposes them to the MCP stdio server.
///     Provider / agent / TUI registrations are accepted (so plugin <c>Initialize</c> never
///     throws) but are not surfaced over MCP — only tools are.
/// </summary>
internal sealed class McpPluginLoadHost : IPluginLoadHost
{
    private readonly ConcurrentDictionary<string, ITool> _tools = new(StringComparer.Ordinal);
    private readonly IServiceCollection _services = new ServiceCollection();
    private readonly IConfiguration _configuration = new ConfigurationBuilder().Build();

    public McpPluginLoadHost(ILoggerFactory loggerFactory, IEventBus eventBus)
    {
        LoggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
        EventBus = eventBus ?? throw new ArgumentNullException(nameof(eventBus));
    }

    public IServiceCollection Services => _services;
    public IConfiguration Configuration => _configuration;
    public ILoggerFactory LoggerFactory { get; }
    public IEventBus EventBus { get; }

    public IReadOnlyDictionary<string, ITool> Tools => _tools;

    public Result RegisterTool(ITool tool)
        => _tools.TryAdd(tool.Name.Value, tool)
            ? Result.Success()
            : Result.Failure($"Tool '{tool.Name.Value}' is already registered; skipping duplicate.");

    public Result RegisterProvider(ProviderId providerId, Func<ILlmClient> factory)
    {
        LoggerFactory.CreateLogger<McpPluginLoadHost>()
            .LogInformation("Plugin provider '{Id}' is not exposed over MCP; ignoring.", providerId);
        return Result.Success();
    }

    public Result RegisterAgent(AgentDefinition agent)
    {
        LoggerFactory.CreateLogger<McpPluginLoadHost>()
            .LogInformation("Plugin agent '{Name}' is not exposed over MCP; ignoring.", agent.Name);
        return Result.Success();
    }

    public Result RegisterTuiPlugin(ITuiPlugin plugin)
    {
        LoggerFactory.CreateLogger<McpPluginLoadHost>()
            .LogInformation("Plugin TUI '{Name}' is not exposed over MCP; ignoring.", plugin.Name);
        return Result.Success();
    }

    public Result RegisterPanelProvider(IPanelProvider panel)
    {
        LoggerFactory.CreateLogger<McpPluginLoadHost>()
            .LogInformation("Plugin panel '{Id}' is not exposed over MCP; ignoring.", panel.Id);
        return Result.Success();
    }
}
