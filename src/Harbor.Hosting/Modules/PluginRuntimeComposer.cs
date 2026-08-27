#if HARBOR_WITH_PLUGINS
using Harbor.Abstractions.Agents;
using Harbor.Abstractions.Events;
using Harbor.Abstractions.Providers;
using Harbor.Abstractions.Tools;
using Harbor.Plugins.Abstractions;
using Harbor.Plugins.Compilation;
using Harbor.Plugins.Hosting;
using Harbor.Plugins.Instantiation;
using Harbor.Plugins.Registration;
using Harbor.Plugins.Storage;
using Harbor.Ui.Framework.Panels;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
namespace Harbor.Hosting;

/// <summary>
///     Single place that composes the CS-plugin load pipeline (source → trust gate →
///     cached compiler → instantiator → registrar). Used by startup discovery
///     (<see cref="RegistriesModule" />) and by the runtime hot-reload runner
///     (<see cref="PluginReloadService" />) so both observe identical trust and cache
///     semantics.
/// </summary>
internal static class PluginRuntimeComposer
{
    /// <summary>
    ///     Build a load host + plugin runtime pair over the supplied live registries.
    /// </summary>
    /// <param name="services">
    ///     Service collection exposed to plugin contexts. Startup passes the real graph's
    ///     collection; hot-reload passes an empty throwaway one — late-loaded plugins
    ///     cannot mutate the already-built container (documented limitation).
    /// </param>
    /// <param name="configuration">Host configuration snapshot.</param>
    /// <param name="loggerFactory">Logger factory.</param>
    /// <param name="eventBus">Live event bus singleton.</param>
    /// <param name="toolRegistry">Live tool registry (post-freeze registrations supported).</param>
    /// <param name="providerRegistry">Live provider registry.</param>
    /// <param name="agentRegistry">Live agent registry.</param>
    /// <param name="panelRegistry">Live panel registry singleton.</param>
    /// <param name="globalPluginsDir">User-managed scope, trusted implicitly.</param>
    /// <param name="projectPluginsDir">Project-local scope, gated by the trust store.</param>
    /// <param name="trustPrompt">
    ///     Optional first-sight approval hook for project-local plugins. Startup passes an
    ///     interactive console prompt; hot-reload passes null so unapproved or edited
    ///     plugins fail closed instead of interrupting a live session.
    /// </param>
    /// <returns>The wired pair, ready for <see cref="PluginHost.LoadAllAsync" />.</returns>
    public static (PluginLoadHost Host, PluginHost Runtime) Compose(
        IServiceCollection services,
        IConfiguration configuration,
        ILoggerFactory loggerFactory,
        IEventBus eventBus,
        IToolRegistry toolRegistry,
        IProviderRegistry providerRegistry,
        IAgentRegistry agentRegistry,
        PanelRegistry panelRegistry,
        string globalPluginsDir,
        string projectPluginsDir,
        Func<PluginScript, Task<bool>>? trustPrompt)
    {
        var loadHost = new PluginLoadHost(
            services,
            configuration ?? new ConfigurationBuilder().Build(),
            loggerFactory,
            eventBus,
            toolRegistry,
            providerRegistry,
            agentRegistry,
            panelRegistry);

        string pluginsCacheDir = Path.Combine(globalPluginsDir, "cache");
        var references = new PluginAssemblyReferences(
            loggerFactory.CreateLogger<PluginAssemblyReferences>());

        var runtime = new PluginHostBuilder()
            .WithSource(BuildTrustedSource(globalPluginsDir, projectPluginsDir, loggerFactory, trustPrompt))
            .WithCompiler(new CachingCompiler(
                new RoslynPluginCompiler(references),
                pluginsCacheDir,
                loggerFactory.CreateLogger<CachingCompiler>()))
            .WithInstantiator(new ReflectionPluginInstantiator())
            .WithRegistrar(new SafePluginRegistrar(
                new PluginRegistrar(globalPluginsDir, loggerFactory.CreateLogger<PluginRegistrar>(), loggerFactory),
                loggerFactory.CreateLogger<SafePluginRegistrar>()))
            .WithOptions(o => o.PluginRoot = globalPluginsDir)
            .Build(loggerFactory.CreateLogger<PluginHost>());

        return (loadHost, runtime);
    }

    /// <summary>
    ///     Trust-gated discovery over both scopes: the user-managed global directory is
    ///     trusted implicitly, project-local scripts go through a persisted per
    ///     path+hash decision with optional interactive approval on first sight.
    /// </summary>
    private static IPluginSource BuildTrustedSource(
        string globalPluginsDir,
        string projectPluginsDir,
        ILoggerFactory loggerFactory,
        Func<PluginScript, Task<bool>>? trustPrompt)
    {
        var inner = new FileSystemPluginSource(
            new[] { globalPluginsDir, projectPluginsDir },
            loggerFactory.CreateLogger<FileSystemPluginSource>());

        var policy = new FileTrustPolicy(
            new[] { globalPluginsDir },
            Path.Combine(globalPluginsDir, "trust.json"),
            loggerFactory.CreateLogger<FileTrustPolicy>(),
            trustPrompt);

        return new TrustingPluginSource(inner, policy, loggerFactory.CreateLogger<TrustingPluginSource>());
    }
}
#endif
