using Harbor.Abstractions.Agents;
using Harbor.Abstractions.Events;
using Harbor.Abstractions.Providers;
using Harbor.Abstractions.Tools;
using Harbor.Telemetry;
#if HARBOR_WITH_PLUGINS
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
        var toolRegistry = ToolsCatalog.CreateToolRegistry(ctx, mcpRegistry, agentRegistry);
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
        var pluginHost = new PluginLoadHost(
            services,
            ctx.Options.Configuration ?? new ConfigurationBuilder().Build(),
            ctx.LoggerFactory,
            eventBus,
            toolRegistry,
            providerRegistry,
            agentRegistry,
            panelRegistry);

        string globalPluginsDir = Path.Combine(harborDir, "plugins");
        string projectPluginsDir = Path.Combine(Directory.GetCurrentDirectory(), ".harbor", "plugins");
        string pluginsCacheDir = Path.Combine(globalPluginsDir, "cache");
        var pluginReferences = new PluginAssemblyReferences(
            ctx.LoggerFactory.CreateLogger<PluginAssemblyReferences>());

        var pluginRuntime = new PluginHostBuilder()
            .WithSource(new FileSystemPluginSource(
                new[] { globalPluginsDir, projectPluginsDir },
                ctx.LoggerFactory.CreateLogger<FileSystemPluginSource>()))
            .WithCompiler(new CachingCompiler(
                new RoslynPluginCompiler(pluginReferences),
                pluginsCacheDir,
                ctx.LoggerFactory.CreateLogger<CachingCompiler>()))
            .WithInstantiator(new ReflectionPluginInstantiator())
            .WithRegistrar(new SafePluginRegistrar(
                new PluginRegistrar(globalPluginsDir, ctx.LoggerFactory.CreateLogger<PluginRegistrar>(), ctx.LoggerFactory),
                ctx.LoggerFactory.CreateLogger<SafePluginRegistrar>()))
            .WithOptions(o => o.PluginRoot = globalPluginsDir)
            .Build(ctx.LoggerFactory.CreateLogger<PluginHost>());
#pragma warning disable RS0030 // Sync-over-async at startup — same pattern as config load.
        var pluginResult = pluginRuntime.LoadAllAsync(pluginHost).GetAwaiter().GetResult();
#pragma warning restore RS0030
        if (pluginResult.IsSuccess)
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
#endif
}
