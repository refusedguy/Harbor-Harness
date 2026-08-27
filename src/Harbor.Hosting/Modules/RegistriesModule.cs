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
            trustPrompt: script => PromptForProjectPluginTrustAsync(script, ctx.Logger));

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
    ///     Interactive first-sight approval for project-local plugins at startup.
    ///     Non-interactive hosts fail closed and skip unreviewed plugins (ROADMAP v0.5
    ///     trust prompt). Accepted decisions are persisted by <see cref="FileTrustPolicy" />.
    /// </summary>
    private static async Task<bool> PromptForProjectPluginTrustAsync(PluginScript script, ILogger log)
    {
        if (Console.IsInputRedirected || Console.IsOutputRedirected)
        {
            log.LogWarning(
                "Non-interactive run: project-local plugin {Path} skipped. Run interactively to review it, or remove it from .harbor/plugins",
                script.Path);
            return false;
        }

        Console.WriteLine();
        Console.WriteLine("Harbor found a new or changed project-local plugin:");
        Console.WriteLine($"  path : {script.Path}");
        Console.WriteLine($"  sha256: {script.Hash[..Math.Min(12, script.Hash.Length)]}…");
        Console.WriteLine("Project plugins execute in-process with full trust — only approve code you reviewed.");
        Console.Write("Trust and load this plugin? [y/N] ");

        string answer = await Console.In.ReadLineAsync().ConfigureAwait(false) ?? string.Empty;
        bool trusted = answer.Trim().Equals("y", StringComparison.OrdinalIgnoreCase)
                       || answer.Trim().Equals("yes", StringComparison.OrdinalIgnoreCase);
        if (!trusted)
            log.LogInformation("Project-local plugin {Path} not approved by the user", script.Path);
        return trusted;
    }
#endif
}
