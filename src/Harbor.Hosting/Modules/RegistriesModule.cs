#if HARBOR_WITH_PLUGINS
using Harbor.Abstractions.Plugins;
using Harbor.Plugins.Hosting;
#endif
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

// DI014/DI016 (Excubo): the plugin reload services register factories that
// resolve through the provider deliberately (documented pattern).
#pragma warning disable DI014, DI016

namespace Harbor.Hosting;

internal static class RegistriesModule
{
    /// <summary>
    ///     Publishes the eager registries into the container through ONE
    ///     facade step (#1592): <see cref="RegistryFacade.Compose" /> builds,
    ///     wires, freezes and publishes agents / mcp / tools / providers /
    ///     panels; the deferred forwarders are its implementation detail and
    ///     never leave that method. What remains here is the plugin route
    ///     (out-of-proc — the CLI registers nothing in-process, #1055s3),
    ///     layered on top of the already-published graph (di-design §3.5 order).
    /// </summary>
    internal static IServiceCollection AddHarborRegistries(
        this IServiceCollection services,
        HarborCompositionContext ctx)
    {
        RegistriesModuleLog.RegisteringAgentsToolsProviders(ctx.Logger);

        RegistryFacade.Compose(services, ctx);

#if HARBOR_WITH_PLUGINS
        StartupPluginLoad startupLoad = LoadPlugins(ctx);
        services.AddSingleton(startupLoad);
        services.AddSingleton(sp =>
        {
            // #1738: the panel reads the SAME inventory the agent-facing
            // plugin_manager tool reads, so the two cannot disagree about what
            // is installed. The inventory is published by RegistryFacade.Compose.
            var reload = new PluginReloadService(
                sp.GetRequiredService<IPluginInventory>(),
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
#else
        RegistriesModuleLog.PluginRuntimeDisabled(ctx.Logger);
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
            RegistriesModuleLog.PluginsViaHost(ctx.Logger, scriptCount);
        else
            RegistriesModuleLog.PluginsHostMissing(ctx.Logger, scriptCount);
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
        if (PluginHostLocator.IsHostAvailable())
            RegistriesModuleLog.PluginsOffNoScripts(ctx.Logger);
        else
            RegistriesModuleLog.PluginsOffNoHost(ctx.Logger);
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

/// <summary>
///     SG4: BCL <c>[LoggerMessage]</c> delegates for <see cref="RegistriesModule" />.
///     Templates, levels and operands are 1-to-1 with the former <c>LogX</c> calls.
/// </summary>
internal static partial class RegistriesModuleLog
{
    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "Registering agents, tools, providers")]
    public static partial void RegisteringAgentsToolsProviders(ILogger logger);

    [LoggerMessage(EventId = 2, Level = LogLevel.Information, Message = "MCP transports registered: {Names}")]
    public static partial void McpTransportsRegistered(ILogger logger, string names);

    [LoggerMessage(EventId = 3, Level = LogLevel.Information, Message = "Plugin runtime disabled (HarborWithPlugins=false)")]
    public static partial void PluginRuntimeDisabled(ILogger logger);

    [LoggerMessage(EventId = 4, Level = LogLevel.Information, Message = "plugins: via host (out-of-proc, {Count} script(s) — tools arrive over MCP 'harbor-csharp-plugins')")]
    public static partial void PluginsViaHost(ILogger logger, int count);

    [LoggerMessage(EventId = 5, Level = LogLevel.Warning, Message = "plugins: {Count} script(s) need harbor-plugins-host (missing) — out-of-proc route unavailable, in-process compile removed (#1055s3), skipping")]
    public static partial void PluginsHostMissing(ILogger logger, int count);

    [LoggerMessage(EventId = 6, Level = LogLevel.Information, Message = "plugins: off (no scripts)")]
    public static partial void PluginsOffNoScripts(ILogger logger);

    [LoggerMessage(EventId = 7, Level = LogLevel.Information, Message = "plugins: off (no host)")]
    public static partial void PluginsOffNoHost(ILogger logger);
}
