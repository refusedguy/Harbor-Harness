using Harbor.Abstractions.Agents;
using Harbor.Abstractions.Plugins;
using Harbor.Abstractions.Providers;
using Harbor.Abstractions.Sessions;
using Harbor.Abstractions.Tools;
using Harbor.Telemetry;
using Harbor.Ui.Framework.Panels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
#if HARBOR_WITH_PLUGINS
using Harbor.Plugins.Hosting;
#endif

// DI014/DI016 (Excubo): the registries are eager artifacts, so a temporary
// provider is constructed deliberately to resolve logger factories for
// registry construction (documented pattern) — same story as RegistriesModule.
#pragma warning disable DI014, DI016

namespace Harbor.Hosting;

/// <summary>
///     Facade (#1592): the SINGLE build step of the eager registries. Builds
///     agents / mcp / tools / providers / panels, wires the deferred
///     forwarders for the not-yet-constructible dependencies (#165,
///     F4-decouple), freezes, and publishes everything as singletons
///     (di-design §3.5 order). The forwarders are created and attached
///     HERE — the attach-wiring is part of this step, and the graph that
///     leaves the method already contains the real <c>ISubAgentRunner</c>
///     rather than the stubs: the two-phase assembly protocol is a
///     facade-internal detail, not a public seam.
/// </summary>
internal static class RegistryFacade
{
    /// <summary>
    ///     One step: ready registries without stubs into <paramref name="services" />
    ///     and <paramref name="ctx" />.
    /// </summary>
    internal static void Compose(IServiceCollection services, HarborCompositionContext ctx)
    {
        var agentRegistry = ToolsCatalog.CreateAgentRegistry(ctx);

        // #477/#1576: remote MCP transports are a registration seam. The resolver is
        // built once here (the composition root, #1576) from the builtins +
        // whatever the host passed through HarborComposeOptions.McpTransports,
        // injected into the registry (which both validates the config name and
        // dispatches on it) and published into the container so plugins/hosts
        // can enumerate the same set.
        var mcpTransports = McpTransportResolver.Compose(ctx.Options.McpTransports);
        foreach (IMcpTransportFactory mcpTransport in mcpTransports.Factories)
            services.AddSingleton<IMcpTransportFactory>(mcpTransport);
        services.AddSingleton(mcpTransports);
        RegistriesModuleLog.McpTransportsRegistered(ctx.Logger, string.Join(", ", mcpTransports.SupportedNames));

        var mcpRegistry = ToolsCatalog.CreateMcpRegistry(ctx, mcpTransports);
        // #1592 (facade detail): the deferred pair. The tools are built EAGERLY
        // here, but their real dependencies (ISessionStore — registered later
        // in AddHarborStorage; IAgentLoop — DI-built) only exist inside the
        // container. The forwarders close that gap: the tools hold them now,
        // and the real instances attach at the single attach point below —
        // the first resolution of ISubAgentRunner in the container.
        var subAgents = new Harbor.Application.Agents.DeferredSubAgentRunner();
        // #165: peer-supervision tools hold the other forwarder of the same
        // pair until the container can build the real store (same
        // eager-registry gap — both attach points live in this step).
        var supervisedSessions = new Harbor.Tools.Builtin.DeferredSessionStore();
        var backgroundTasks = new Harbor.Application.Agents.BackgroundTaskRegistry(
            ctx.LoggerFactory.CreateLogger<Harbor.Application.Agents.BackgroundTaskRegistry>());
        services.AddSingleton<Harbor.Abstractions.Agents.IBackgroundTaskRegistry>(backgroundTasks);
        // LSP facade: one manager for the whole process — the `lsp` tool and the
        // desktop editor share the same auto-spawned language servers + cache.
        var lspService = new Harbor.Lsp.LspManager(
            ctx.LoggerFactory.CreateLogger<Harbor.Lsp.LspManager>());
        services.AddSingleton<Harbor.Abstractions.Lsp.ILspService>(lspService);

        // #1748: the tool-output spill area. ONE instance serves both sides —
        // the context boundary writes an oversized tool output into it, and the
        // `spill_read` tool reads it back. The tool registry is built EAGERLY
        // here, before the container exists, so the store is a plain object
        // constructed here and published into the container as the same
        // instance: the file the model is pointed at and the file the tool
        // reads are the same file, not two stores' files.
        var spillStore = new Harbor.Application.Filesystem.FileToolOutputSpillStore(
            ctx.LoggerFactory.CreateLogger<Harbor.Application.Filesystem.FileToolOutputSpillStore>(),
            ttl: TimeSpan.FromHours(ctx.Harbor.ToolOutput.SpillTtlHours));
        services.AddSingleton<IToolOutputSpillStore>(spillStore);

        // #1735: the skill provider and the registry over it are built HERE,
        // eagerly, for the same reason the tool registry is: the skill tool
        // is constructed in the next step and needs the registry, and the
        // registry needs its providers. Both are plain objects — neither the
        // provider (it reads the workspace roots through the shared
        // WorkspaceContextSource) nor the registry (it merges what its
        // providers hand it) needs the container.
        //
        // One instance each, published as singletons: the registry the tool
        // holds and the registry the agent loop resolves are the same object,
        // so the model-facing catalog and the model-facing call cannot drift
        // apart. The product ships the EMPTY invocation matrix here — the
        // per-skill policy in each skill file's front-matter is what narrows a
        // skill today; the matrix is the layer that narrows by request
        // context once a host has rows to give it.
        var skillProvider = new Harbor.Application.Skills.SkillProvider();
        services.AddSingleton<ISkillProvider>(skillProvider);
        var skillRegistry = new Harbor.Application.Skills.SkillRegistry(
            [skillProvider],
            Harbor.Application.Skills.SkillPolicyMatrix.Empty,
            ctx.LoggerFactory.CreateLogger<Harbor.Application.Skills.SkillRegistry>());
        services.AddSingleton<ISkillRegistry>(skillRegistry);

#if HARBOR_WITH_PLUGINS
        // #1738: the plugin INVENTORY — one object, published here and used
        // by BOTH consumers of "what is installed": the agent-facing
        // plugin_manager tool (registered below, through ToolsCatalog) and the
        // /plugins panel (through PluginReloadService). Two inventories would
        // be two answers to one question, and the drift would show up as an
        // agent proposing to remove a plugin the panel does not list.
        IInstalledPluginInventory pluginInventory = new FileSystemPluginInventory(
            Path.Combine(ctx.Options.HarborDir, "plugins"),
            Path.Combine(Directory.GetCurrentDirectory(), ".harbor", "plugins"),
            ctx.LoggerFactory.CreateLogger<FileSystemPluginInventory>());
        services.AddSingleton<IInstalledPluginInventory>(pluginInventory);
#else
        // No plugin runtime in this build: the inventory does not exist and the
        // plugin_manager tool is not registered (ToolsCatalog asks for it).
        IInstalledPluginInventory? pluginInventory = null;
#endif

        // The provider registry is built BEFORE the tool registry (#1732): the
        // `harbor_inspect` tool reports the providers this runtime has
        // registered, and it reads that registry live. Nothing in the provider
        // registry depends on the tool registry, so the order was free; it is
        // now chosen rather than inherited from the sequence the two lines
        // happened to be written in.
        var providerRegistry = ProviderFactories.CreateProviderRegistry(ctx, services);

        var toolRegistry = ToolsCatalog.CreateToolRegistry(ctx, mcpRegistry, agentRegistry, providerRegistry, subAgents, backgroundTasks, lspService, supervisedSessions, skillRegistry, spillStore, pluginInventory);
        // The single attach point of both forwarders: the container resolves
        // ISubAgentRunner only after ISessionStore exists, so the attach runs
        // exactly once and inside the facade — HostBuilder's warmup
        // resolution does exactly that on startup.
        services.AddSingleton<Harbor.Abstractions.Agents.ISubAgentRunner>(sp =>
        {
            var store = sp.GetRequiredService<Harbor.Abstractions.Sessions.ISessionStore>();
            var real = new Harbor.Application.Agents.SubAgentRunner(
                store,
                sp.GetRequiredService<Harbor.Abstractions.Agents.IAgentLoop>(),
                sp.GetRequiredService<Microsoft.Extensions.Logging.ILoggerFactory>()
                    .CreateLogger<Harbor.Application.Agents.SubAgentRunner>());
            subAgents.Attach(real);
            supervisedSessions.Attach(store);
            return real;
        });
        var panelRegistry = new PanelRegistry(ctx.LoggerFactory.CreateLogger<PanelRegistry>());

        // #562: publish the bundle before Freeze() so a module that runs
        // before this one reads ctx.Registries gets a named
        // InvalidOperationException rather than a member typed non-nullable
        // and holding null. (#1055s3: no in-process plugin pipeline mutates
        // the registries any more — plugin tools arrive over MCP, and the
        // plugin-contributed backend maps stay at their empty defaults.)
        //
        // Assigning the bundle before Freeze() is deliberate and does not weaken
        // the §3.5 invariant: Freeze() mutates the registry OBJECTS, and every
        // consumer of the four registries gets them either from the
        // instrumented views published below (after the freeze) or from a later
        // module in AddHarbor's chain.
        ctx.SetRegistries(new HarborRegistries(agentRegistry, toolRegistry, providerRegistry, panelRegistry));

        toolRegistry.Freeze();
        providerRegistry.Freeze();

        // sprint3-C C1: instrument at the DI boundary. Consumers resolving
        // the interfaces get the instrumented views, never the raw registries.
        services.AddSingleton<IToolRegistry>(new InstrumentedToolRegistry(
            toolRegistry, MeterMetrics.Instance, ActivityTracer.Instance));
        services.AddSingleton<IProviderRegistry>(new InstrumentedProviderRegistry(
            providerRegistry, MeterMetrics.Instance, ActivityTracer.Instance));
        services.AddSingleton<IAgentRegistry>(agentRegistry);
        services.AddSingleton<IMcpRegistry>(mcpRegistry);
        services.AddSingleton(panelRegistry);
        services.AddSingleton<IPanelRegistry>(panelRegistry);
    }
}
