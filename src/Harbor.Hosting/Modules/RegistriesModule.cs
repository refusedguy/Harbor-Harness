using Harbor.Abstractions.Agents;
using Harbor.Abstractions.Providers;
using Harbor.Abstractions.Tools;
using Harbor.Telemetry;
#if HARBOR_WITH_PLUGINS
using Harbor.Plugins.Hosting;
#endif
using Harbor.Ui.Framework.Panels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

// DI014/DI016 (Excubo): the registries are eager artifacts, so a temporary
// provider is constructed deliberately to resolve logger factories for
// registry construction (documented pattern).
#pragma warning disable DI014, DI016

namespace Harbor.Hosting;

internal static class RegistriesModule
{
    /// <summary>
    ///     Builds the eager registries (agents / mcp / tools / providers /
    ///     panels), notes the plugin route (out-of-proc — the CLI registers
    ///     nothing in-process), freezes, then publishes everything as
    ///     singletons (di-design §3.5 order).
    /// </summary>
    internal static IServiceCollection AddHarborRegistries(
        this IServiceCollection services,
        HarborCompositionContext ctx)
    {
        ctx.Logger.LogInformation("Registering agents, tools, providers");

        var agentRegistry = ToolsCatalog.CreateAgentRegistry(ctx);

        // #477: remote MCP transports are a registration seam. The resolver is
        // built once from the builtins + whatever the host passed through
        // HarborComposeOptions.McpTransports, handed to the registry (which
        // both validates the config name and dispatches on it) and published
        // into the container so plugins/hosts can enumerate the same set.
        var mcpTransports = McpTransportResolver.Compose(ctx.Options.McpTransports);
        foreach (IMcpTransportFactory mcpTransport in mcpTransports.Factories)
            services.AddSingleton<IMcpTransportFactory>(mcpTransport);
        services.AddSingleton(mcpTransports);
        ctx.Logger.LogInformation(
            "MCP transports registered: {Names}", string.Join(", ", mcpTransports.SupportedNames));

        var mcpRegistry = ToolsCatalog.CreateMcpRegistry(ctx, mcpTransports);
        // Sub-agent runner: TaskTool is built EAGERLY here, but its real dependencies
        // (ISessionStore — registered later in AddHarborStorage; IAgentLoop — DI-built)
        // only exist inside the container. The deferred forwarder closes that gap: the
        // tool holds it now, the real runner attaches on first resolution (F4-decouple).
        var subAgents = new Harbor.Application.Agents.DeferredSubAgentRunner();
        // #165: peer-supervision tools hold this forwarder until the container
        // can build the real store (same eager-registry gap as subAgents).
        var supervisedSessions = new Harbor.Tools.Builtin.DeferredSessionStore();
        var backgroundTasks = new Harbor.Application.Agents.BackgroundTaskRegistry(
            ctx.LoggerFactory.CreateLogger<Harbor.Application.Agents.BackgroundTaskRegistry>());
        services.AddSingleton<Harbor.Abstractions.Agents.IBackgroundTaskRegistry>(backgroundTasks);
        // LSP facade: one manager for the whole process — the `lsp` tool and the
        // desktop editor share the same auto-spawned language servers + cache.
        var lspService = new Harbor.Lsp.LspManager(
            ctx.LoggerFactory.CreateLogger<Harbor.Lsp.LspManager>());
        services.AddSingleton<Harbor.Abstractions.Lsp.ILspService>(lspService);
        var toolRegistry = ToolsCatalog.CreateToolRegistry(ctx, mcpRegistry, agentRegistry, subAgents, backgroundTasks, lspService, supervisedSessions);
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
        var providerRegistry = ProviderFactories.CreateProviderRegistry(ctx, services);
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

#if HARBOR_WITH_PLUGINS
        StartupPluginLoad startupLoad = LoadPlugins(ctx);
#else
        ctx.Logger.LogInformation("Plugin runtime disabled (HarborWithPlugins=false)");
#endif

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

#if HARBOR_WITH_PLUGINS
        services.AddSingleton(startupLoad);
        services.AddSingleton(sp =>
        {
            var reload = new PluginReloadService(
                ctx.Options.HarborDir,
                sp.GetRequiredService<ILoggerFactory>().CreateLogger<PluginReloadService>());
            // The startup load is always empty out-of-process; Attach fires
            // immediately so the /plugins panel seeds with zero plugins.
            startupLoad.Attach(reload.NoteLoaded);
            return reload;
        });
        services.AddSingleton(sp => new PluginAutoReloader(
            sp.GetRequiredService<PluginReloadService>(),
            ctx.Options.HarborDir,
            autoReloadEnabled: ctx.Harbor.Tooling.AutoReloadPlugins,
            sp.GetRequiredService<ILoggerFactory>()));
#endif

        return services;
    }

#if HARBOR_WITH_PLUGINS
    /// <summary>
    ///     Startup plugin pipeline (#1055, slice 3): the default route is the
    ///     out-of-process host over MCP. The CLI process itself never compiles
    ///     CS plugins — not synchronously, not in the background — so startup
    ///     never waits for plugins and never loads Roslyn. Returns an
    ///     already-completed empty load; plugin tools arrive over MCP
    ///     (<c>harbor-csharp-plugins</c>, registered by
    ///     <c>ToolsCatalog.CreateMcpRegistry</c> when the host binary ships
    ///     next to the CLI).
    /// </summary>
    private static StartupPluginLoad LoadPlugins(HarborCompositionContext ctx)
    {
        string harborDir = ctx.Options.HarborDir;
        string globalPluginsDir = Path.Combine(harborDir, "plugins");
        string projectPluginsDir = Path.Combine(Directory.GetCurrentDirectory(), ".harbor", "plugins");

        // #1055 slice 1 (graceful absence): no scripts anywhere — nothing to
        // serve. Report the absence in exactly one line. Startup stays fast,
        // the registries stay full, the exit stays normal.
        int scriptCount = CountPluginScripts(globalPluginsDir) + CountPluginScripts(projectPluginsDir);
        if (scriptCount == 0)
            return ReportPluginAbsence(ctx);

        // #1055 slice 3: scripts exist, so they are served out-of-process.
        // When the host binary ships next to the CLI the MCP route above
        // already picked it up; when it is missing there is nowhere to
        // compile them — the in-process Roslyn path is removed, not kept as
        // a fallback — so skip with one honest line either way.
        if (PluginHostLocator.IsHostAvailable())
            ctx.Logger.LogInformation(
                "plugins: via host (out-of-proc, {Count} script(s) — tools arrive over MCP 'harbor-csharp-plugins')",
                scriptCount);
        else
            ctx.Logger.LogWarning(
                "plugins: {Count} script(s) need harbor-plugins-host (missing) — out-of-proc route unavailable, in-process compile removed (#1055s3), skipping",
                scriptCount);
        return StartupPluginLoad.Empty();
    }

    /// <summary>
    ///     #1055 slice 1: when neither plugin scope holds a <c>.cs</c> script there
    ///     is nothing to compose. Reports the absence in exactly one line — the
    ///     host-binary probe decides which one — and returns an already-completed
    ///     empty load so late consumers observe zero plugins.
    /// </summary>
    private static StartupPluginLoad ReportPluginAbsence(HarborCompositionContext ctx)
    {
        // The binary ships next to the CLI; when it is missing too, "no host" is
        // the honest reason. When it is present, the host is simply idle.
        ctx.Logger.LogInformation(
            PluginHostLocator.IsHostAvailable() ? "plugins: off (no scripts)" : "plugins: off (no host)");
        return StartupPluginLoad.Empty();
    }

    private static int CountPluginScripts(string dir)
    {
        try
        {
            return Directory.Exists(dir) ? Directory.GetFiles(dir, "*.cs").Length : 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Unreadable directory reads as absent — the host would skip it too.
            return 0;
        }
    }
#endif
}
