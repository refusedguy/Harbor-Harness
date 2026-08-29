using Harbor.Abstractions.Agents;
using Harbor.Abstractions.Events;
using Harbor.Abstractions.Providers;
using Harbor.Abstractions.Tools;
using Harbor.Telemetry;
#if HARBOR_WITH_PLUGINS
using Harbor.Plugins.Abstractions;
using Harbor.Plugins.Compilation;
using Harbor.Plugins.Hosting;
using Harbor.Plugins.Instantiation;
using Harbor.Plugins.Registration;
using Harbor.Plugins.Storage;
#endif
using Harbor.Ui.Framework.Panels;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

// DI014/DI016 (Excubo): the registries are eager artifacts — plugins mutate
// them before Freeze, so a temporary provider is constructed deliberately to
// resolve logger factories for registry construction (documented pattern).
#pragma warning disable DI014, DI016

namespace Harbor.Hosting;

internal static class RegistriesModule
{
    /// <summary>
    ///     Builds the eager registries (agents / mcp / tools / providers /
    ///     panels), runs the plugin pipeline BEFORE Freeze, freezes, then
    ///     publishes everything as singletons (di-design §3.5 order).
    /// </summary>
    internal static IServiceCollection AddHarborRegistries(
        this IServiceCollection services,
        HarborCompositionContext ctx)
    {
        ctx.Logger.LogInformation("Registering agents, tools, providers");

        var agentRegistry = ToolsCatalog.CreateAgentRegistry(ctx);
        var mcpRegistry = ToolsCatalog.CreateMcpRegistry(ctx);
        // Sub-agent runner: TaskTool is built EAGERLY here, but its real dependencies
        // (ISessionStore — registered later in AddHarborStorage; IAgentLoop — DI-built)
        // only exist inside the container. The deferred forwarder closes that gap: the
        // tool holds it now, the real runner attaches on first resolution (F4-decouple).
        var subAgents = new Harbor.Application.Agents.DeferredSubAgentRunner();
        var toolRegistry = ToolsCatalog.CreateToolRegistry(ctx, mcpRegistry, agentRegistry, subAgents);
        services.AddSingleton<Harbor.Abstractions.Agents.ISubAgentRunner>(sp =>
        {
            var real = new Harbor.Application.Agents.SubAgentRunner(
                sp.GetRequiredService<Harbor.Abstractions.Sessions.ISessionStore>(),
                sp.GetRequiredService<Harbor.Abstractions.Agents.IAgentLoop>(),
                sp.GetRequiredService<Microsoft.Extensions.Logging.ILoggerFactory>()
                    .CreateLogger<Harbor.Application.Agents.SubAgentRunner>());
            subAgents.Attach(real);
            return real;
        });
        var providerRegistry = ProviderFactories.CreateProviderRegistry(ctx, services);
        var eventBus = ctx.EventBus;
        var panelRegistry = new PanelRegistry(ctx.LoggerFactory.CreateLogger<PanelRegistry>());

#if HARBOR_WITH_PLUGINS
        LoadPlugins(services, ctx, eventBus, toolRegistry, providerRegistry, agentRegistry, panelRegistry);
#else
        ctx.Logger.LogInformation("Plugin runtime disabled (HarborWithPlugins=false)");
#endif

        toolRegistry.Freeze();
        providerRegistry.Freeze();

        // sprint3-C C1: instrument at the DI boundary. Plugins keep mutating the
        // RAW registries (ctx.Registries) before Freeze; consumers resolving the
        // interfaces get the instrumented views.
        services.AddSingleton<IToolRegistry>(new InstrumentedToolRegistry(
            toolRegistry, MeterMetrics.Instance, ActivityTracer.Instance));
        services.AddSingleton<IProviderRegistry>(new InstrumentedProviderRegistry(
            providerRegistry, MeterMetrics.Instance, ActivityTracer.Instance));
        services.AddSingleton<IAgentRegistry>(agentRegistry);
        services.AddSingleton<IMcpRegistry>(mcpRegistry);
        services.AddSingleton(panelRegistry);
        services.AddSingleton<IPanelRegistry>(panelRegistry);

        ctx.Registries.Agents = agentRegistry;
        ctx.Registries.Tools = toolRegistry;
        ctx.Registries.Providers = providerRegistry;
        ctx.Registries.Panels = panelRegistry;

#if HARBOR_WITH_PLUGINS
        services.AddSingleton(sp => new PluginReloadService(
            sp,
            ctx.Options.HarborDir,
            ctx.Options.Configuration ?? new ConfigurationBuilder().Build(),
            sp.GetRequiredService<ILoggerFactory>().CreateLogger<PluginReloadService>()));
        services.AddSingleton(sp => new PluginAutoReloader(
            sp.GetRequiredService<PluginReloadService>(),
            ctx.Options.HarborDir,
            autoReloadEnabled: ctx.Harbor.Tooling.AutoReloadPlugins,
            sp.GetRequiredService<ILoggerFactory>()));
#endif

        return services;
    }

#if HARBOR_WITH_PLUGINS
    private static void LoadPlugins(
        IServiceCollection services,
        HarborCompositionContext ctx,
        IEventBus eventBus,
        IToolRegistry toolRegistry,
        IProviderRegistry providerRegistry,
        IAgentRegistry agentRegistry,
        PanelRegistry panelRegistry)
    {
        string harborDir = ctx.Options.HarborDir;
        string globalPluginsDir = Path.Combine(harborDir, "plugins");
        string projectPluginsDir = Path.Combine(Directory.GetCurrentDirectory(), ".harbor", "plugins");

        // Per-capability approval (trust.json v2) when an interactive console is
        // available; non-interactive hosts get no prompt hook at all — DecideAsync
        // then fails closed and unreviewed project-local plugins are skipped.
        bool interactive = !Console.IsInputRedirected && !Console.IsOutputRedirected;

        var (pluginHost, pluginRuntime) = PluginRuntimeComposer.Compose(
            services,
            ctx.Options.Configuration ?? new ConfigurationBuilder().Build(),
            ctx.LoggerFactory,
            eventBus,
            toolRegistry,
            providerRegistry,
            agentRegistry,
            panelRegistry,
            globalPluginsDir,
            projectPluginsDir,
            trustPrompt: null,
            capabilityPrompt: interactive
                ? (script, declared) => PromptForPluginCapabilitiesAsync(script, declared, ctx.Logger)
                : null);

#pragma warning disable RS0030 // Sync-over-async at startup — same pattern as config load.
        var pluginResult = pluginRuntime.LoadAllAsync(pluginHost).GetAwaiter().GetResult();
#pragma warning restore RS0030
        if (pluginResult.IsSuccess) // §4.6-ok: ветка логирования успеха/провала, не конверсия.
        {
            ctx.Logger.LogInformation("Loaded {Count} CS plugin(s)", pluginResult.Value.Count);
            foreach (var p in pluginResult.Value)
            {
                ctx.Logger.LogInformation("  - {DisplayName} (from cache: {FromCache})", p.DisplayName, p.LoadedFromCache);
            }
        }
        else
        {
            ctx.Logger.LogWarning("CS plugin loading failed: {Error}", pluginResult.Error);
        }
    }

    /// <summary>
    ///     Interactive per-capability approval for project-local plugins at startup
    ///     (trust.json v2). The user approves each manifest-declared capability
    ///     individually; the approved subset is persisted by
    ///     <see cref="FileTrustPolicy" /> keyed by path + sha256. Declining everything
    ///     still loads the plugin with zero capabilities — fully sandboxed. Unknown
    ///     capability tokens are rejected before any prompt (fail-closed).
    /// </summary>
    private static async Task<IReadOnlySet<PluginCapability>> PromptForPluginCapabilitiesAsync(
        PluginScript script,
        IReadOnlySet<PluginCapability> declared,
        ILogger log)
    {
        var approved = new HashSet<PluginCapability>();

        if (script.HasInvalidManifest)
        {
            log.LogWarning(
                "Plugin {Path} declares an unknown capability token — refusing to grant anything (fail-closed)",
                script.Path);
            return approved;
        }

        Console.WriteLine();
        Console.WriteLine("Harbor found a new or changed project-local plugin:");
        Console.WriteLine($"  path : {script.Path}");
        Console.WriteLine($"  sha256: {script.Hash[..Math.Min(12, script.Hash.Length)]}…");
        Console.WriteLine("Plugins execute in-process — approve each capability individually.");
        Console.WriteLine("Declining everything loads the plugin with no capabilities (fully sandboxed).");

        if (declared.Count == 0)
        {
            Console.WriteLine("  (manifest declares no capabilities — nothing to approve)");
            return approved;
        }

        int index = 0;
        foreach (PluginCapability capability in declared)
        {
            index++;
            Console.Write($"  [{index}/{declared.Count}] {PluginCapabilities.ToName(capability)} [y/N] ");
            string answer = await Console.In.ReadLineAsync().ConfigureAwait(false) ?? string.Empty;
            if (answer.Trim() is "y" or "yes")
                approved.Add(capability);
        }

        log.LogInformation(
            "Project-local plugin {Path}: approved capabilities [{Capabilities}]",
            script.Path,
            approved.Count == 0 ? "none" : string.Join(",", approved.Select(PluginCapabilities.ToName)));
        return approved;
    }
#endif
}
