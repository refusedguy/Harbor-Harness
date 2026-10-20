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
/// </summary>
/// <remarks>
///     <para>
///         This route exposes TOOLS ONLY. Every other door therefore REFUSES the
///         contribution and says why, instead of accepting it silently: a door that
///         answered <c>Result.Success()</c> while registering nothing would tell a
///         plugin its provider / agent / panel / session store / TUI backend was
///         registered when it was not — the "accepted" and the "dropped" were
///         indistinguishable to the caller (#1758).
///     </para>
///     <para>
///         The refusal is ANSWERED, never thrown, so a plugin's <c>Initialize</c>
///         still does not fail over an axis this route does not carry — the
///         registration is reported as refused and the plugin load continues.
///     </para>
/// </remarks>
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
        McpPluginLoadHostLog.ProviderRefused(LoggerFactory.CreateLogger<McpPluginLoadHost>(), providerId);
        return Result.Failure(
            $"The out-of-process plugin host exposes tools only; provider '{providerId}' was not registered.");
    }

    public Result RegisterAgent(AgentDefinition agent)
    {
        McpPluginLoadHostLog.AgentRefused(LoggerFactory.CreateLogger<McpPluginLoadHost>(), agent.Name);
        return Result.Failure(
            $"The out-of-process plugin host exposes tools only; agent '{agent.Name}' was not registered.");
    }

    public Result RegisterTuiPlugin(ITuiPlugin plugin)
    {
        McpPluginLoadHostLog.TuiRefused(LoggerFactory.CreateLogger<McpPluginLoadHost>(), plugin.Name);
        return Result.Failure(
            $"The out-of-process plugin host exposes tools only; TUI plugin '{plugin.Name}' was not registered.");
    }

    public Result RegisterPanelProvider(IPanelProvider panel)
    {
        McpPluginLoadHostLog.PanelRefused(LoggerFactory.CreateLogger<McpPluginLoadHost>(), panel.Id);
        return Result.Failure(
            $"The out-of-process plugin host exposes tools only; panel '{panel.Id}' was not registered.");
    }

    // #581: the MCP stdio server exposes tools only. These two doors exist so the
    // interface stays uniformly implementable — an MCP-hosted plugin calling them
    // gets a loud "not exposed" log and an explicit refusal instead of a compile
    // error against a sealed seam, and instead of a Success that registered
    // nothing (#1758).
    public Result RegisterSessionStore(string backendId, Func<ISessionStore> factory)
    {
        McpPluginLoadHostLog.SessionStoreRefused(LoggerFactory.CreateLogger<McpPluginLoadHost>(), backendId);
        return Result.Failure(
            $"The out-of-process plugin host exposes tools only; session store '{backendId}' was not registered.");
    }

    public Result RegisterTuiBackend(string backendId, IReadOnlyList<string>? aliases, Func<ITuiRenderer> factory)
    {
        McpPluginLoadHostLog.TuiBackendRefused(LoggerFactory.CreateLogger<McpPluginLoadHost>(), backendId);
        return Result.Failure(
            $"The out-of-process plugin host exposes tools only; TUI backend '{backendId}' was not registered.");
    }
}

/// <summary>
///     SG10: BCL <c>[LoggerMessage]</c> delegates for <see cref="McpPluginLoadHost" />.
///     Templates, levels and operands are 1-to-1 with the former <c>LogX</c> calls.
/// </summary>
internal static partial class McpPluginLoadHostLog
{
    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "Plugin provider '{Id}' is not exposed over MCP; refusing the registration.")]
    public static partial void ProviderRefused(ILogger logger, string id);

    [LoggerMessage(EventId = 2, Level = LogLevel.Information, Message = "Plugin agent '{Name}' is not exposed over MCP; refusing the registration.")]
    public static partial void AgentRefused(ILogger logger, string name);

    [LoggerMessage(EventId = 3, Level = LogLevel.Information, Message = "Plugin TUI '{Name}' is not exposed over MCP; refusing the registration.")]
    public static partial void TuiRefused(ILogger logger, string name);

    [LoggerMessage(EventId = 4, Level = LogLevel.Information, Message = "Plugin panel '{Id}' is not exposed over MCP; refusing the registration.")]
    public static partial void PanelRefused(ILogger logger, string id);

    [LoggerMessage(EventId = 5, Level = LogLevel.Information, Message = "Plugin session store '{Id}' is not exposed over MCP; refusing the registration.")]
    public static partial void SessionStoreRefused(ILogger logger, string id);

    [LoggerMessage(EventId = 6, Level = LogLevel.Information, Message = "Plugin TUI backend '{Id}' is not exposed over MCP; refusing the registration.")]
    public static partial void TuiBackendRefused(ILogger logger, string id);
}
