using Harbor.Abstractions.Agents;
using Harbor.Abstractions.Events;
using Harbor.Abstractions.Models.Identifiers;
using Harbor.Abstractions.Providers;
using Harbor.Abstractions.Tools;
using Harbor.Ui.Framework.Panels;
using Harbor.Terminal.Abstractions.Plugins;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
namespace Harbor.Plugins.Abstractions;

/// <summary>
///     Sink that the Roslyn-based CS plugin loader calls into when a plugin contributes
///     tools, providers, agents, or TUI extensions. Implementations live in the host
///     composition root (e.g. <c>Harbor.Cli</c>); the loader only depends on this
///     abstraction so it can stay free of <c>Harbor.Core</c>.
/// </summary>
/// <remarks>
///     <para>
///         This is intentionally distinct from <c>Harbor.Abstractions.Plugins.IPluginHost</c>
///         (which manages full plugin load/unload lifecycle). <c>IPluginLoadHost</c> is the
///     narrower "registration sink" called by <see cref="CsPluginLoader" /> while it is
///     wiring up a freshly-compiled plugin.
///     </para>
///     <para>
///         Implementations MUST be thread-safe: <see cref="RegisterTool" />,
///         <see cref="RegisterProvider" />, <see cref="RegisterAgent" />, and
///         <see cref="RegisterTuiPlugin" /> may be called concurrently when multiple plugins
///         are loaded in parallel.
///     </para>
/// </remarks>
public interface IPluginLoadHost
{
    /// <summary>
    ///     The host's <see cref="IServiceCollection" />. Plugins receive this via
    ///     <see cref="Harbor.Abstractions.Plugins.PluginContext.Services" /> and may register
    ///     additional services here. Only effective when the loader runs BEFORE the host's
    ///     service provider is built.
    /// </summary>
    IServiceCollection Services { get; }

    /// <summary>
    ///     The host's <see cref="IConfiguration" />. Passed through to plugins via
    ///     <see cref="Harbor.Abstractions.Plugins.PluginContext.Configuration" />.
    /// </summary>
    IConfiguration Configuration { get; }

    /// <summary>
    ///     The host's <see cref="ILoggerFactory" />. Used both for the loader's own
    ///     diagnostics and for <see cref="Harbor.Abstractions.Plugins.PluginContext.LoggerFactory" />.
    /// </summary>
    ILoggerFactory LoggerFactory { get; }

    /// <summary>
    ///     The host's <see cref="IEventBus" />. Passed through to plugins so they can
    ///     subscribe to <see cref="AgentEvent" />s.
    /// </summary>
    IEventBus EventBus { get; }

    /// <summary>
    ///     Register a tool instance contributed by a plugin.
    /// </summary>
    /// <param name="tool">The tool to register.</param>
    /// <returns>Success, or failure with an error message (e.g. name collision).</returns>
    Result RegisterTool(ITool tool);

    /// <summary>
    ///     Register an LLM provider contributed by a plugin. The factory is invoked lazily
    ///     the first time the provider is needed.
    /// </summary>
    /// <param name="providerId">The provider id to register under.</param>
    /// <param name="factory">Factory producing the <see cref="ILlmClient" />.</param>
    /// <returns>Success, or failure with an error message.</returns>
    Result RegisterProvider(ProviderId providerId, Func<ILlmClient> factory);

    /// <summary>
    ///     Register an agent contributed by a plugin.
    /// </summary>
    /// <param name="agent">The agent definition to register.</param>
    /// <returns>Success, or failure with an error message (e.g. name collision).</returns>
    Result RegisterAgent(AgentDefinition agent);

    /// <summary>
    ///     Register a TUI plugin contributed by a CS plugin. The host defers actual view /
    ///     view-model registration until the TUI renderer is constructed.
    /// </summary>
    /// <param name="plugin">The TUI plugin to register.</param>
    /// <returns>Success, or failure with an error message.</returns>
    Result RegisterTuiPlugin(ITuiPlugin plugin);

    /// <summary>
    ///     Register a dockable <see cref="IPanelProvider" /> contributed by an
    ///     <see cref="ITuiPanelPlugin" />. The host stores it; the active
    ///     <c>PanelRegistry</c> picks it up when the TUI renderer starts.
    /// </summary>
    /// <param name="panel">The panel provider to register.</param>
    /// <returns>Success, or failure with an error message (e.g. empty id, name collision).</returns>
    Result RegisterPanelProvider(IPanelProvider panel);
}
