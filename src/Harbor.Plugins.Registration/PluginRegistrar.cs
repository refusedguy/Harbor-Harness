using Harbor.Abstractions.Agents;
using Harbor.Abstractions.Models.Identifiers;
using Harbor.Abstractions.Plugins;
using Harbor.Abstractions.Providers;
using Harbor.Abstractions.Sessions;
using Harbor.Abstractions.Tools;
using Harbor.Plugins.Abstractions;
using Harbor.Plugins.Instantiation;
using Harbor.Terminal.Abstractions;
using Harbor.Terminal.Abstractions.Plugins;
using Harbor.Ui.Framework.Panels;
using Microsoft.Extensions.Logging;
namespace Harbor.Plugins.Registration;
/// <summary>
///     Default <see cref="IPluginRegistrar" />. Builds a <see cref="PluginContext" />,
///     calls <see cref="IPlugin.Initialize" />, then dispatches each <c>Register*</c>
///     method based on which sub-interfaces the plugin implements. Tool / provider / agent /
///     panel registration failures are routed through the host's <see cref="IPluginLoadHost" />
///     <c>Register*</c> methods (which return <see cref="Result" />). <b>#1758:</b> a
///     REFUSED registration FAILS the plugin's registration. It used to be logged and swallowed.
///     <b>#1721:</b> a plugin that declares a <see cref="IPlugin.RequiredHarborVersion" />
///     newer than the running Harbor is refused BEFORE it is initialized, so it contributes
///     nothing to the host.
///     <b>#1739:</b> a plugin that ships a manifest is checked against it — the name, the
///     version and the extension axes it declares must be the ones the plugin reports and
///     implements, or the plugin is refused with the reason.
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
    private readonly ILoggerFactory _loggerFactory;
    private readonly IPluginAuditLog? _audit;
    private readonly Version _harborVersion;

    /// <summary>
    ///     Construct a new registrar.
    /// </summary>
    /// <param name="pluginRoot">
    ///     Host plugin root directory. Used to derive per-plugin data directories.
    /// </param>
    /// <param name="logger">Logger for diagnostics.</param>
    /// <param name="loggerFactory">Logger factory for tool/provider construction.</param>
    /// <param name="audit">Optional capability-audit sink for the tool sandbox.</param>
    /// <param name="harborVersion">
    ///     The Harbor version plugins are checked against (#1721). Defaults to
    ///     <see cref="PluginLifecycle.CurrentHarborVersion" />, the same version the
    ///     plugin is handed in <see cref="PluginContext.HarborVersion" />.
    /// </param>
    public PluginRegistrar(
        string pluginRoot,
        ILogger<PluginRegistrar> logger,
        ILoggerFactory loggerFactory,
        IPluginAuditLog? audit = null,
        Version? harborVersion = null)
    {
        _pluginRoot = pluginRoot ?? throw new ArgumentNullException(nameof(pluginRoot));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _loggerFactory = loggerFactory;
        _audit = audit;
        _harborVersion = harborVersion ?? PluginLifecycle.CurrentHarborVersion;
    }

    /// <inheritdoc />
    public Result Register(LoadedPlugin plugin, IPluginLoadHost host)
    {
        if (plugin is null)
            throw new ArgumentNullException(nameof(plugin));
        if (host is null)
            throw new ArgumentNullException(nameof(host));

        // #1721: the compatibility gate. A plugin that declares a Harbor newer than
        // this runtime must not run at all. No Initialize, no Register*, no tool in
        // the registry. The gate sits here, in front of IPlugin.Initialize, because
        // this is the one seam both load routes pass through: the in-process
        // CsPluginLoader and the out-of-process harbor-plugins-host both register
        // through a PluginRegistrar.
        Version? requiredHarborVersion = plugin.Instance.RequiredHarborVersion;
        if (requiredHarborVersion is null)
        {
            _logger.LogWarning(
                "Plugin {DisplayName} declares no RequiredHarborVersion. Loading it without a compatibility check.",
                plugin.DisplayName);
        }

        var compatibility = PluginCompatibility.Check(
            plugin.Instance.Name,
            requiredHarborVersion,
            _harborVersion);
        if (compatibility.IsFailure)
        {
            _logger.LogWarning(
                "Plugin {DisplayName} is not compatible with this Harbor: {Reason}",
                plugin.DisplayName,
                compatibility.Error);
            return compatibility;
        }

        // #1739: the manifest is the plugin's own declaration, and it is checked
        // against the plugin. The code stays the truth for what the plugin DOES;
        // the manifest says what it CLAIMS, and a claim that does not match the
        // code is refused — a plugin that ships two different names, or two
        // different sets of axes, is a plugin the host cannot describe.
        var markers = PluginMarkers.Of(plugin.Instance);
        var manifestRefusal = CheckManifest(plugin, markers);
        if (manifestRefusal is not null)
        {
            _logger.LogWarning(
                "Plugin {DisplayName} does not match its manifest: {Reason}",
                plugin.DisplayName,
                manifestRefusal);
            return Result.Failure(manifestRefusal);
        }

        var context = PluginLifecycle.BuildContext(host, plugin.Instance, _pluginRoot, plugin.SourcePath);
        var initResult = PluginLifecycle.Initialize(plugin.Instance, context);
        if (initResult.IsFailure)
            return initResult;

        var recorder = new RecordingPluginLoadHost(host);
        // Result.Try (CSharpFunctionalExtensions 3.7.0) wraps a throwing call in a
        // Result: same catch scope, same text. The nested panel-plugin catch below
        // is deliberately kept. It logs and CONTINUES rather than failing the whole.
        var outcome = Result.Try(
            () =>
            {
                // #1758: every door is called through the recorder, so a refusal
                // is remembered and can fail the registration at the end. The
                // recorder forwards the host's own Result unchanged.
                //
                // The markers come from `markers` — the ONE list that says which
                // axes this plugin implements. The dispatch below and the manifest
                // check above read the same list, so a new axis cannot be
                // dispatched without being declared, or declared without being
                // dispatched.
                if (markers.Tool is { } toolPlugin)
                {
                    toolPlugin.RegisterTools(new ToolRegistryBuilderAdapter(
                        recorder,
                        plugin.Instance.Name,
                        plugin.DeclaredCapabilities,
                        _audit,
                        _logger,
                        _loggerFactory));
                }
                if (markers.Provider is { } providerPlugin)
                {
                    providerPlugin.RegisterProviders(new ProviderRegistryBuilderAdapter(recorder, _logger, _loggerFactory));
                }
                if (markers.Agent is { } agentPlugin)
                {
                    agentPlugin.RegisterAgents(new AgentRegistryBuilderAdapter(recorder, _logger));
                }
                if (markers.Tui is { } tuiPlugin)
                {
                    var r = recorder.RegisterTuiPlugin(tuiPlugin);
                    if (r.IsFailure)
                        _logger.LogWarning("Failed to register TUI plugin {Name}: {Error}", tuiPlugin.Name, r.Error);
                }
                if (markers.Panel is { } panelPlugin)
                {
                    // Give the plugin a thin IPanelRegistry adapter that routes Register()
                    // calls back into the host. The host stores them; the active
                    // PanelRegistry (owned by the SpectreTUI renderer) picks them up when
                    // the renderer starts.
                    try
                    {
                        panelPlugin.RegisterPanels(new PanelRegistryPluginAdapter(recorder, _logger));
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "ITuiPanelPlugin.RegisterPanels threw for {Name}", panelPlugin.Name);
                    }
                }

                // #620: these two doors have been on IPluginLoadHost since #581/#584
                // with the registries, the composition plumbing and the module wiring
                // already built around them — but nothing dispatched them and a plugin
                // is never handed the host, so they were unreachable. ExtensionAxisRegistrationRule
                // found both axes half-open; these branches are the other half of the fix.
                if (markers.SessionStore is { } storePlugin)
                {
                    try
                    {
                        storePlugin.RegisterSessionStores(new SessionStoreRegistrarAdapter(recorder, _logger));
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "ISessionStorePlugin.RegisterSessionStores threw for {Name}", plugin.Name);
                    }
                }
                if (markers.TuiBackend is { } backendPlugin)
                {
                    try
                    {
                        backendPlugin.RegisterTuiBackends(new TuiBackendRegistrarAdapter(recorder, _logger));
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "ITuiBackendPlugin.RegisterTuiBackends threw for {Name}", plugin.Name);
                    }
                }
            },
            ex => $"Register threw: {ex.Message}");

        if (outcome.IsFailure)
            return outcome;

        // #1758: a door that refused a registration did not register anything, so
        // the plugin did not arrive wired. Reporting Success here is what made
        // "accepted" and "silently dropped" the same answer to the caller.
        return recorder.ToResult();
    }

    // ── The manifest gate (#1739) ───────────────────────────────────────────────

    /// <summary>
    ///     Why the plugin does not match the manifest it shipped, or
    ///     <c>null</c> when it does.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Three things are checked, and each of them is a claim the manifest
    ///         makes about the plugin:
    ///     </para>
    ///     <list type="bullet">
    ///         <item>
    ///             <description>
    ///                 The Harbor version the manifest asks for. It goes through
    ///                 the same gate as the in-code floor, so the manifest and
    ///                 the code cannot disagree about what the rule is.
    ///             </description>
    ///         </item>
    ///         <item>
    ///             <description>
    ///                 The name and the version. A manifest that names the
    ///                 plugin differently from the plugin itself is a plugin the
    ///                 host cannot describe — an index built from the manifest
    ///                 would list a plugin that does not exist.
    ///             </description>
    ///         </item>
    ///         <item>
    ///             <description>
    ///                 The extension axes. The manifest says what the plugin
    ///                 contributes; the plugin says what it implements. When the
    ///                 manifest declares axes, the two must be the same set.
    ///             </description>
    ///         </item>
    ///     </list>
    ///     <para>
    ///         A manifest that declares NOTHING is not a disagreement: it simply
    ///         says less than the code, and the code wins.
    ///     </para>
    /// </remarks>
    /// <param name="plugin">The loaded plugin.</param>
    /// <param name="markers">The extension axes the plugin implements.</param>
    /// <returns>The reason to refuse the plugin, or <c>null</c>.</returns>
    private string? CheckManifest(LoadedPlugin plugin, PluginMarkers markers)
    {
        PluginManifest? manifest = plugin.Manifest;
        if (manifest is null)
            return null;

        if (manifest.RequiredHarborVersion is { } floor)
        {
            var compatibility = PluginCompatibility.Check(plugin.Instance.Name, floor, _harborVersion);
            if (compatibility.IsFailure)
                return compatibility.Error;
        }

        if (manifest.Name is { } declaredName
            && !string.Equals(declaredName, plugin.Instance.Name, StringComparison.Ordinal))
        {
            return $"The manifest of plugin '{plugin.Instance.Name}' declares the name '{declaredName}', "
                   + $"but the plugin reports '{plugin.Instance.Name}'. The manifest and the plugin must "
                   + "agree, or the manifest must not declare a name.";
        }

        if (manifest.Version is { } declaredVersion
            && !PluginCompatibility.IsSameVersion(declaredVersion, plugin.Instance.Version))
        {
            return $"The manifest of plugin '{plugin.Instance.Name}' declares the version '{declaredVersion}', "
                   + $"but the plugin reports '{plugin.Instance.Version}'. The manifest and the plugin must "
                   + "agree, or the manifest must not declare a version.";
        }

        if (manifest.Axes is { } declaredAxes)
        {
            var declared = new HashSet<string>(declaredAxes, StringComparer.Ordinal);
            var implemented = new HashSet<string>(markers.Names, StringComparer.Ordinal);
            if (!declared.SetEquals(implemented))
            {
                return $"The manifest of plugin '{plugin.Instance.Name}' declares the extension axes "
                       + $"[{string.Join(", ", declared.Order(StringComparer.Ordinal))}], but the plugin "
                       + $"implements [{string.Join(", ", implemented.Order(StringComparer.Ordinal))}]. The "
                       + "manifest and the plugin must agree, or the manifest must not declare axes.";
            }
        }

        return null;
    }

    /// <summary>
    ///     The extension-axis markers a plugin implements.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         This is the ONE list of markers the registrar knows. The dispatch
    ///         below reads it, and the manifest check above reads it, so an axis
    ///         cannot be dispatched without being declared, or declared in a
    ///         manifest without being dispatched.
    ///     </para>
    ///     <para>
    ///         The axis NAMES are the type names — the same spelling
    ///         <c>PluginAxisRegistry</c> uses, and the spelling a manifest writes
    ///         down.
    ///     </para>
    /// </remarks>
    /// <param name="Names">The names of the axes the plugin implements.</param>
    /// <param name="Tool">The tool axis, when the plugin implements it.</param>
    /// <param name="Provider">The provider axis, when the plugin implements it.</param>
    /// <param name="Agent">The agent axis, when the plugin implements it.</param>
    /// <param name="Tui">The TUI-plugin axis, when the plugin implements it.</param>
    /// <param name="Panel">The panel axis, when the plugin implements it.</param>
    /// <param name="SessionStore">The session-store axis, when the plugin implements it.</param>
    /// <param name="TuiBackend">The TUI-backend axis, when the plugin implements it.</param>
    private sealed record PluginMarkers(
        IReadOnlyList<string> Names,
        IToolPlugin? Tool,
        IProviderPlugin? Provider,
        IAgentPlugin? Agent,
        ITuiPlugin? Tui,
        ITuiPanelPlugin? Panel,
        ISessionStorePlugin? SessionStore,
        ITuiBackendPlugin? TuiBackend)
    {
        /// <summary>
        ///     Read the markers off a plugin instance.
        /// </summary>
        /// <remarks>
        ///     <para>
        ///         The <c>is</c> checks below ARE the axis table: each one names a
        ///         marker the registrar dispatches. The dispatch reads the result,
        ///         and the manifest check compares the manifest against it, so the
        ///         list of axes has one home and cannot drift from what is
        ///         dispatched.
        ///     </para>
        ///     <para>
        ///         The checks are written in the <c>is</c> form on purpose: the
        ///         architecture rule that grades the axes finds a dispatched
        ///         marker by that shape
        ///         (<c>tests/Harbor.Architecture.Tests/ExtensionAxisRegistrationRule.cs</c>).
        ///     </para>
        /// </remarks>
        /// <param name="instance">The live plugin.</param>
        /// <returns>The markers it implements.</returns>
        public static PluginMarkers Of(IPlugin instance)
        {
            ArgumentNullException.ThrowIfNull(instance);

            IToolPlugin? tool = instance is IToolPlugin typedTool ? typedTool : null;
            IProviderPlugin? provider = instance is IProviderPlugin typedProvider ? typedProvider : null;
            IAgentPlugin? agent = instance is IAgentPlugin typedAgent ? typedAgent : null;
            ITuiPlugin? tui = instance is ITuiPlugin typedTui ? typedTui : null;
            ITuiPanelPlugin? panel = instance is ITuiPanelPlugin typedPanel ? typedPanel : null;
            ISessionStorePlugin? sessionStore =
                instance is ISessionStorePlugin typedStore ? typedStore : null;
            ITuiBackendPlugin? tuiBackend =
                instance is ITuiBackendPlugin typedBackend ? typedBackend : null;

            var names = new List<string>(7);
            AddMarker(tool, names);
            AddMarker(provider, names);
            AddMarker(agent, names);
            AddMarker(tui, names);
            AddMarker(panel, names);
            AddMarker(sessionStore, names);
            AddMarker(tuiBackend, names);

            return new PluginMarkers(
                names,
                tool,
                provider,
                agent,
                tui,
                panel,
                sessionStore,
                tuiBackend);
        }

        /// <summary>
        ///     Add the axis name of a marker the plugin implements.
        /// </summary>
        /// <remarks>
        ///     The name is the type's simple name, which is the spelling
        ///     <c>PluginAxisRegistry</c> uses and the spelling a manifest writes.
        /// </remarks>
        /// <typeparam name="T">The marker interface.</typeparam>
        /// <param name="marker">The marker, or <c>null</c> when the plugin does not implement it.</param>
        /// <param name="names">The list to add to.</param>
        private static void AddMarker<T>(T? marker, List<string> names)
            where T : class
        {
            if (marker is not null)
                names.Add(typeof(T).Name);
        }
    }

    // ── Backend registrar adapters ───────────────────────────────────────────────
    // #620: one-method sinks so a plugin reaches its OWN door and no other. Handing a
    // plugin the whole IPluginLoadHost would work and would quietly reopen the tool,
    // provider, agent and panel axes to a plugin that only wanted a session store.

    /// <summary>Forwards one axis' door into the host, logging rather than throwing on a rejected registration.</summary>
    private static void ReportDoorFailure(ILogger logger, string kind, string id, Result result)
    {
        if (result.IsFailure)
            logger.LogWarning("Plugin {Kind} registration failed for '{Id}': {Error}", kind, id, result.Error);
    }

    private sealed class SessionStoreRegistrarAdapter : ISessionStoreRegistrar
    {
        private readonly IPluginLoadHost _host;
        private readonly ILogger _logger;

        internal SessionStoreRegistrarAdapter(IPluginLoadHost host, ILogger logger)
        {
            _host = host;
            _logger = logger;
        }

        public Result RegisterSessionStore(string backendId, Func<ISessionStore> factory)
        {
            Result result = _host.RegisterSessionStore(backendId, factory);
            ReportDoorFailure(_logger, "session store", backendId, result);
            return result;
        }
    }

    private sealed class TuiBackendRegistrarAdapter : ITuiBackendRegistrar
    {
        private readonly IPluginLoadHost _host;
        private readonly ILogger _logger;

        internal TuiBackendRegistrarAdapter(IPluginLoadHost host, ILogger logger)
        {
            _host = host;
            _logger = logger;
        }

        public Result RegisterTuiBackend(string backendId, IReadOnlyList<string>? aliases, Func<ITuiRenderer> factory)
        {
            Result result = _host.RegisterTuiBackend(backendId, aliases, factory);
            ReportDoorFailure(_logger, "TUI backend", backendId, result);
            return result;
        }
    }

    // ── Registry builder adapters ────────────────────────────────────────────────
    // Wrap IPluginLoadHost's Register* methods into the IToolRegistryBuilder /
    // IProviderRegistryBuilder / IAgentRegistryBuilder shapes that IToolPlugin etc. expect.

    private sealed class ToolRegistryBuilderAdapter : IToolRegistryBuilder
    {
        private readonly IPluginLoadHost _host;
        private readonly string _pluginName;
        private readonly IReadOnlySet<PluginCapability> _pluginCapabilities;
        private readonly IPluginAuditLog? _audit;
        private readonly ILogger _logger;
        private readonly ILoggerFactory _loggerFactory;

        internal ToolRegistryBuilderAdapter(
            IPluginLoadHost host,
            string pluginName,
            IReadOnlySet<PluginCapability> pluginCapabilities,
            IPluginAuditLog? audit,
            ILogger logger,
            ILoggerFactory loggerFactory)
        {
            _host = host;
            _pluginName = pluginName;
            _pluginCapabilities = pluginCapabilities;
            _audit = audit;
            _logger = logger;
            _loggerFactory = loggerFactory;
        }

        public void AddTool(ITool tool)
        {
            // Every plugin-contributed tool passes through the execution sandbox:
            // 30s timeout + 10MB allocation guard, with PluginBlockedEvent on enforcement.
            var sandboxed = new SandboxedPluginTool(
                tool,
                _pluginName,
                _host.EventBus,
                _host.LoggerFactory.CreateLogger<SandboxedPluginTool>(),
                capabilities: _pluginCapabilities,
                audit: _audit);
            var r = _host.RegisterTool(sandboxed);
            if (r.IsFailure)
                _logger.LogWarning("Plugin tool registration failed for {Name}: {Error}", tool.Name, r.Error);
        }

        public void AddTool<T>() where T : ITool, new() => AddTool(new T());
        public void AddTool(Func<ITool> factory) => AddTool(factory());
        public void AddTool(IToolFactory factory) => AddTool(factory.CreateTool(_loggerFactory));
        public void AddTool(Func<ILoggerFactory, ITool> factory) => AddTool(factory(_loggerFactory));
    }

    private sealed class ProviderRegistryBuilderAdapter : IProviderRegistryBuilder
    {
        private readonly IPluginLoadHost _host;
        private readonly ILogger _logger;
        private readonly ILoggerFactory _loggerFactory;

        internal ProviderRegistryBuilderAdapter(IPluginLoadHost host, ILogger logger, ILoggerFactory loggerFactory)
        {
            _host = host;
            _logger = logger;
            _loggerFactory = loggerFactory;
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
        public void AddProvider(IProviderFactory factory)
        {
            var r = _host.RegisterProvider(factory.ProviderId, () => factory.CreateClient(_loggerFactory));
            if (r.IsFailure)
                _logger.LogWarning("Plugin provider registration failed for {Id}: {Error}", factory.ProviderId, r.Error);
        }

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
