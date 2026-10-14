using System.Collections.Concurrent;
using CSharpFunctionalExtensions;
using Harbor.Abstractions.Agents;
using Harbor.Abstractions.Events;
using Harbor.Abstractions.Models.Identifiers;
using Harbor.Abstractions.Providers;
using Harbor.Abstractions.Sessions;
using Harbor.Abstractions.Tools;
using Harbor.Plugins.Abstractions;
using Harbor.Terminal.Abstractions;
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
        McpPluginLoadHostLog.ProviderIgnored(LoggerFactory.CreateLogger<McpPluginLoadHost>(), providerId);
        return Result.Success();
    }

    public Result RegisterAgent(AgentDefinition agent)
    {
        McpPluginLoadHostLog.AgentIgnored(LoggerFactory.CreateLogger<McpPluginLoadHost>(), agent.Name);
        return Result.Success();
    }

    public Result RegisterTuiPlugin(ITuiPlugin plugin)
    {
        McpPluginLoadHostLog.TuiIgnored(LoggerFactory.CreateLogger<McpPluginLoadHost>(), plugin.Name);
        return Result.Success();
    }

    public Result RegisterPanelProvider(IPanelProvider panel)
    {
        McpPluginLoadHostLog.PanelIgnored(LoggerFactory.CreateLogger<McpPluginLoadHost>(), panel.Id);
        return Result.Success();
    }

    // #581: the MCP stdio server exposes tools only. These two doors exist so the
    // interface stays uniformly implementable — an MCP-hosted plugin calling them
    // gets a loud "not exposed" log instead of a compile error against a sealed seam.
    public Result RegisterSessionStore(string backendId, Func<ISessionStore> factory)
    {
        McpPluginLoadHostLog.SessionStoreIgnored(LoggerFactory.CreateLogger<McpPluginLoadHost>(), backendId);
        return Result.Success();
    }

    public Result RegisterTuiBackend(string backendId, IReadOnlyList<string>? aliases, Func<ITuiRenderer> factory)
    {
        McpPluginLoadHostLog.TuiBackendIgnored(LoggerFactory.CreateLogger<McpPluginLoadHost>(), backendId);
        return Result.Success();
    }
}

/// <summary>
///     SG10: BCL <c>[LoggerMessage]</c> delegates for <see cref="McpPluginLoadHost" />.
///     Templates, levels and operands are 1-to-1 with the former <c>LogX</c> calls.
/// </summary>
internal static partial class McpPluginLoadHostLog
{
    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "Plugin provider '{Id}' is not exposed over MCP; ignoring.")]
    public static partial void ProviderIgnored(ILogger logger, string id);

    [LoggerMessage(EventId = 2, Level = LogLevel.Information, Message = "Plugin agent '{Name}' is not exposed over MCP; ignoring.")]
    public static partial void AgentIgnored(ILogger logger, string name);

    [LoggerMessage(EventId = 3, Level = LogLevel.Information, Message = "Plugin TUI '{Name}' is not exposed over MCP; ignoring.")]
    public static partial void TuiIgnored(ILogger logger, string name);

    [LoggerMessage(EventId = 4, Level = LogLevel.Information, Message = "Plugin panel '{Id}' is not exposed over MCP; ignoring.")]
    public static partial void PanelIgnored(ILogger logger, string id);

    [LoggerMessage(EventId = 5, Level = LogLevel.Information, Message = "Plugin session store '{Id}' is not exposed over MCP; ignoring.")]
    public static partial void SessionStoreIgnored(ILogger logger, string id);

    [LoggerMessage(EventId = 6, Level = LogLevel.Information, Message = "Plugin TUI backend '{Id}' is not exposed over MCP; ignoring.")]
    public static partial void TuiBackendIgnored(ILogger logger, string id);
}
