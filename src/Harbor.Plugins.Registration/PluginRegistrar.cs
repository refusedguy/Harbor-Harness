using Harbor.Abstractions.Agents;
using Harbor.Abstractions.Models.Identifiers;
using Harbor.Abstractions.Plugins;
using Harbor.Abstractions.Providers;
using Harbor.Abstractions.Tools;
using Harbor.Plugins.Abstractions;
using Harbor.Plugins.Instantiation;
using Harbor.Terminal.Abstractions.Plugins;
using Harbor.Ui.Framework.Panels;
using Microsoft.Extensions.Logging;
namespace Harbor.Plugins.Registration;
/// <summary>
///     Default <see cref="IPluginRegistrar" />. Builds a <see cref="PluginContext" />,
///     calls <see cref="IPlugin.Initialize" />, then dispatches each <c>Register*</c>
///     method based on which sub-interfaces the plugin implements. Tool / provider / agent /
///     panel registration failures are routed through the host's <see cref="IPluginLoadHost" />
///     <c>Register*</c> methods (which return <see cref="Result" />); the registrar logs but
///     does not abort on per-item failures.
/// </summary>
/// <remarks>
///     <para>
///         The registrar takes a <c>pluginRoot</c> (e.g. <c>~/.harbor/plugins</c>) at
///         construction so it can derive
///         <see cref="PluginContext.DataDirectory" /> = <c>{pluginRoot}/data/{plugin.Name}</c>.
///     </para>
/// </remarks>
public sealed class PluginRegistrar : IPluginRegistrar
{
    private readonly ILogger<PluginRegistrar> _logger;
    private readonly string _pluginRoot;

    /// <summary>
    ///     Construct a new registrar.
    /// </summary>
    /// <param name="pluginRoot">
    ///     Host plugin root directory. Used to derive per-plugin data directories.
    /// </param>
    /// <param name="logger">Logger for diagnostics.</param>
    public PluginRegistrar(string pluginRoot, ILogger<PluginRegistrar> logger)
    {
        _pluginRoot = pluginRoot ?? throw new ArgumentNullException(nameof(pluginRoot));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public Result Register(LoadedPlugin plugin, IPluginLoadHost host)
    {
        if (plugin is null)
            throw new ArgumentNullException(nameof(plugin));
        if (host is null)
            throw new ArgumentNullException(nameof(host));

        var context = PluginLifecycle.BuildContext(host, plugin.Instance, _pluginRoot, plugin.SourcePath);
        var initResult = PluginLifecycle.Initialize(plugin.Instance, context);
        if (initResult.IsFailure)
            return initResult;

        try
        {
            if (plugin.Instance is IToolPlugin toolPlugin)
            {
                toolPlugin.RegisterTools(new ToolRegistryBuilderAdapter(host, _logger));
            }
            if (plugin.Instance is IProviderPlugin providerPlugin)
            {
                providerPlugin.RegisterProviders(new ProviderRegistryBuilderAdapter(host, _logger));
            }
            if (plugin.Instance is IAgentPlugin agentPlugin)
            {
                agentPlugin.RegisterAgents(new AgentRegistryBuilderAdapter(host, _logger));
            }
            if (plugin.Instance is ITuiPlugin tuiPlugin)
            {
                var r = host.RegisterTuiPlugin(tuiPlugin);
                if (r.IsFailure)
                    _logger.LogWarning("Failed to register TUI plugin {Name}: {Error}", tuiPlugin.Name, r.Error);
            }
            if (plugin.Instance is ITuiPanelPlugin panelPlugin)
            {
                // Give the plugin a thin IPanelRegistry adapter that routes Register()
                // calls back into the host. The host stores them; the active
                // PanelRegistry (owned by the SpectreTUI renderer) picks them up when
                // the renderer starts.
                try
                {
                    panelPlugin.RegisterPanels(new PanelRegistryPluginAdapter(host, _logger));
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "ITuiPanelPlugin.RegisterPanels threw for {Name}", panelPlugin.Name);
                }
            }
        }
        catch (Exception ex)
        {
            return Result.Failure($"Register threw: {ex.Message}");
        }

        return Result.Success();
    }

    // ── Registry builder adapters ────────────────────────────────────────────────
    // Wrap IPluginLoadHost's Register* methods into the IToolRegistryBuilder /
    // IProviderRegistryBuilder / IAgentRegistryBuilder shapes that IToolPlugin etc. expect.

    private sealed class ToolRegistryBuilderAdapter : IToolRegistryBuilder
    {
        private readonly IPluginLoadHost _host;
        private readonly ILogger _logger;

        internal ToolRegistryBuilderAdapter(IPluginLoadHost host, ILogger logger)
        {
            _host = host;
            _logger = logger;
        }

        public void AddTool(ITool tool)
        {
            var r = _host.RegisterTool(tool);
            if (r.IsFailure)
                _logger.LogWarning("Plugin tool registration failed for {Name}: {Error}", tool.Name, r.Error);
        }

        public void AddTool<T>() where T : ITool, new() => AddTool(new T());
        public void AddTool(Func<ITool> factory) => AddTool(factory());
    }

    private sealed class ProviderRegistryBuilderAdapter : IProviderRegistryBuilder
    {
        private readonly IPluginLoadHost _host;
        private readonly ILogger _logger;

        internal ProviderRegistryBuilderAdapter(IPluginLoadHost host, ILogger logger)
        {
            _host = host;
            _logger = logger;
        }

        /// <summary>
        ///     Register a provider by invoking the factory once to read
        ///     <see cref="ILlmClient.ProviderId" />, then delegating to the
        ///     host's <c>RegisterProvider(ProviderId, Func&lt;ILlmClient&gt;)</c>.
        /// </summary>
        /// <remarks>
        ///     <b>Architecture audit v2 §3.4:</b> this overload eagerly invokes
        ///     <paramref name="factory" /> just to read <c>ProviderId</c>.
        ///     Plugin authors should prefer the explicit-id overload
        ///     <see cref="AddProvider(ProviderId, Func{ILlmClient})" /> which
        ///     never invokes the factory at registration time. The eager
        ///     overload is retained for source compatibility with existing
        ///     plugins that don't know their provider id until they construct
        ///     the client.
        /// </remarks>
        public void AddProvider(Func<ILlmClient> factory)
        {
            // Eager invocation: needed to read ProviderId. Plugin authors
            // who know their provider id upfront should use the
            // AddProvider(ProviderId, Func<ILlmClient>) overload instead.
            var tempClient = factory();
            var r = _host.RegisterProvider(tempClient.ProviderId, factory);
            if (r.IsFailure)
                _logger.LogWarning("Plugin provider registration failed for {Id}: {Error}", tempClient.ProviderId, r.Error);
        }

        public void AddProvider(ProviderId providerId, Func<ILlmClient> factory)
        {
            var r = _host.RegisterProvider(providerId, factory);
            if (r.IsFailure)
                _logger.LogWarning("Plugin provider registration failed for {Id}: {Error}", providerId, r.Error);
        }

        public void AddProvider(string providerId, Func<ILlmClient> factory)
        {
            var pidResult = ProviderId.TryCreate(providerId);
            if (pidResult.IsFailure)
            {
                _logger.LogWarning("Plugin provider registration failed: invalid id '{Id}'", providerId);
                return;
            }
            AddProvider(pidResult.Value, factory);
        }
    }

    private sealed class AgentRegistryBuilderAdapter : IAgentRegistryBuilder
    {
        private readonly IPluginLoadHost _host;
        private readonly ILogger _logger;

        internal AgentRegistryBuilderAdapter(IPluginLoadHost host, ILogger logger)
        {
            _host = host;
            _logger = logger;
        }

        public void AddAgent(AgentDefinition agent)
        {
            var r = _host.RegisterAgent(agent);
            if (r.IsFailure)
                _logger.LogWarning("Plugin agent registration failed for {Name}: {Error}", agent.Name, r.Error);
        }
    }
}
