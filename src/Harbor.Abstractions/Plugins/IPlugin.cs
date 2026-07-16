using Harbor.Abstractions.Agents;
using Harbor.Abstractions.Events;
using Harbor.Abstractions.Providers;
using Harbor.Abstractions.Tools;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
namespace Harbor.Abstractions.Plugins;
/// <summary>
///     Base contract for Harbor plugins. Implements Plugin pattern.
///     Plugins can be:
///     - Tool plugins (register <see cref="Tools.ITool" />)
///     - Provider plugins (register <see cref="Providers.ILlmClient" />)
///     - Agent plugins (register <see cref="Agents.AgentDefinition" />)
///     - Command plugins (register slash-commands)
/// </summary>
/// <remarks>
///     <para>
///         Plugins are the canonical extension point. Each plugin has a stable <see cref="Name" />,
///         a semantic <see cref="Version" />, a minimum <see cref="RequiredHarborVersion" />, and a
///         short human-readable <see cref="Description" /> shown in <c>/plugins</c>.
///     </para>
///     <para>
///         The lifecycle is: <see cref="Initialize" /> on load → <see cref="ShutdownAsync" /> on
///         unload. Implementations MUST be thread-safe for <see cref="ShutdownAsync" /> being
///         called concurrently with active plugin activity.
///     </para>
/// </remarks>
public interface IPlugin
{
    /// <summary>
    ///     Stable, lowercase plugin id (e.g. <c>web-search</c>).
    /// </summary>
    public string Name { get; }

    /// <summary>
    ///     Semantic version of the plugin.
    /// </summary>
    public Version Version { get; }

    /// <summary>
    ///     Minimum Harbor version required by this plugin.
    /// </summary>
    public Version RequiredHarborVersion { get; }

    /// <summary>
    ///     Human-readable description shown in <c>/plugins</c>.
    /// </summary>
    public string Description { get; }

    /// <summary>
    ///     Initialize the plugin. Called once at load time.
    /// </summary>
    /// <param name="context">The plugin context (services, configuration, logger, event bus).</param>
    public void Initialize(PluginContext context);

    /// <summary>
    ///     Shut down the plugin. Called once at unload time. Should release all resources.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task ShutdownAsync(CancellationToken cancellationToken = default);
}

/// <summary>
///     Plugin that contributes one or more tools.
/// </summary>
public interface IToolPlugin : IPlugin
{
    /// <summary>
    ///     Register the plugin's tools into the supplied builder.
    /// </summary>
    /// <param name="builder">The tool registry builder.</param>
    public void RegisterTools(IToolRegistryBuilder builder);
}

/// <summary>
///     Plugin that contributes one or more LLM providers.
/// </summary>
public interface IProviderPlugin : IPlugin
{
    /// <summary>
    ///     Register the plugin's providers into the supplied builder.
    /// </summary>
    /// <param name="builder">The provider registry builder.</param>
    public void RegisterProviders(IProviderRegistryBuilder builder);
}

/// <summary>
///     Plugin that contributes one or more agents.
/// </summary>
public interface IAgentPlugin : IPlugin
{
    /// <summary>
    ///     Register the plugin's agents into the supplied builder.
    /// </summary>
    /// <param name="builder">The agent registry builder.</param>
    public void RegisterAgents(IAgentRegistryBuilder builder);
}

/// <summary>
///     Context passed to plugin initialization.
/// </summary>
public sealed class PluginContext
{
    /// <summary>
    ///     The DI service collection — plugins can register services here.
    /// </summary>
    public required IServiceCollection Services { get; init; }

    /// <summary>
    ///     The application configuration (env vars, JSON config files).
    /// </summary>
    public required IConfiguration Configuration { get; init; }

    /// <summary>
    ///     The logger factory for creating <see cref="ILogger{TCategoryName}" /> instances.
    /// </summary>
    public required ILoggerFactory LoggerFactory { get; init; }

    /// <summary>
    ///     The event bus for subscribing to <see cref="AgentEvent" />s.
    /// </summary>
    public required IEventBus EventBus { get; init; }

    /// <summary>
    ///     The plugin's on-disk directory (read-only).
    /// </summary>
    public required string PluginDirectory { get; init; }

    /// <summary>
    ///     The plugin's data directory (read-write, persisted across runs).
    /// </summary>
    public required string DataDirectory { get; init; }

    /// <summary>
    ///     The current Harbor version.
    /// </summary>
    public Version HarborVersion { get; init; } = new(0, 1, 0);

    /// <summary>
    ///     Convenience factory for creating a typed logger.
    /// </summary>
    /// <typeparam name="T">The category type.</typeparam>
    /// <returns>A typed <see cref="ILogger{T}" />.</returns>
    public ILogger<T> CreateLogger<T>() => LoggerFactory.CreateLogger<T>();
}

/// <summary>
///     Host for plugins — manages discovery, loading, and lifecycle.
/// </summary>
/// <remarks>
///     Implementations live in <c>Harbor.Core</c> (JIT) and a future AOT-compatible
///     out-of-process variant.
/// </remarks>
public interface IPluginHost
{
    /// <summary>
    ///     All plugins currently loaded.
    /// </summary>
    public IReadOnlyList<IPlugin> LoadedPlugins { get; }

    /// <summary>
    ///     Load a plugin from the given path (DLL or assembly reference).
    /// </summary>
    /// <param name="path">The plugin path.</param>
    /// <returns>Success, or failure with an error message.</returns>
    public Result LoadPlugin(string path);

    /// <summary>
    ///     Unload a previously-loaded plugin by name.
    /// </summary>
    /// <param name="name">The plugin name.</param>
    /// <returns>Success, or failure if not loaded.</returns>
    public Result UnloadPlugin(string name);

    /// <summary>
    ///     Shut down all loaded plugins in reverse-load order.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    public Task ShutdownAllAsync(CancellationToken ct = default);
}
