using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Harbor.Abstractions.Agents;
using Harbor.Abstractions.Events;
using Harbor.Abstractions.Permissions;
using Harbor.Abstractions.Providers;
using Harbor.Abstractions.Sessions;
using Harbor.Abstractions.Tools;
using Harbor.Abstractions.Tui;
using Harbor.Cli.Configuration;
using Harbor.Core.Events;
using Harbor.Core.Agents;
using Harbor.Core.Configuration;
using Harbor.Core.Onboarding;
using Harbor.Core.Permissions;
using Harbor.Core.Resilience;
using Harbor.Core.Sessions;
using Harbor.Core.Tools;
using Harbor.Desktop.Abstractions.Configuration;
using Harbor.Ipc.Client;
using Harbor.Ipc.InProcess;
using Harbor.Ipc.Server;
using Harbor.Storage.Jsonl;
using Harbor.Storage.Memory;
using Harbor.Terminal.Abstractions;
using Harbor.Tools.Mcp;
using Harbor.Tui.Plain;
using Harbor.Ui.Framework.Panels;
#if HARBOR_WITH_PLUGINS
using Harbor.Plugins.Compilation;
using Harbor.Plugins.Hosting;
using Harbor.Plugins.Instantiation;
using Harbor.Plugins.Registration;
using Harbor.Plugins.Storage;
#endif
#if HARBOR_WITH_ALL_PROVIDERS
using Harbor.Storage.Sqlite;
#endif
#if HARBOR_WITH_SPECTRE_TUI
using Harbor.Tui.Ansi;
using Harbor.Tui.RazorConsole;
using Harbor.Tui.Spectre;
using Harbor.Tui.Spectre.Fullscreen;
using Harbor.Tui.Termina;
using Harbor.Tui.TerminalGui;
#endif
// A3 (DI analyzers) added Excubo.Analyzers.DependencyInjection rules
// DI014 (BuildServiceProvider should be disposed) and DI016 (don't call
// BuildServiceProvider during composition). The HostBuilder pattern
// constructs a temporary ServiceProvider deliberately so the eagerly
// constructed ToolRegistry/ProviderRegistry/AgentRegistry see the same
// ILoggerFactory / IEventBus that the final ServiceProvider will use —
// this is the documented pattern from sub-agent 1 (Plugins.Runtime) and
// is preserved until a full async-HostBuilder refactor lands.
#pragma warning disable DI014, DI016
namespace Harbor.Cli.Hosting;

internal static partial class HostBuilder
{
    private static void RegisterCore(HostApplicationBuilder builder)
    {
        _logger.LogInformation("Registering core services");
        builder.Services.AddSingleton<IConfigStore>(sp => new JsonConfigStore(
            logger: sp.GetRequiredService<ILogger<JsonConfigStore>>()));
        builder.Services.AddSingleton<AuthStore>();
        builder.Services.AddSingleton<OnboardingWizard>();
        builder.Services.AddSingleton<ITokenTracker, TokenTracker>();
        builder.Services.AddSingleton<IEventBusMiddleware>(sp =>
            new TypeFilterMiddleware(sp.GetRequiredService<ILogger<TypeFilterMiddleware>>()));
        builder.Services.AddSingleton<IEventBus>(sp =>
        {
            var logger = sp.GetRequiredService<ILogger<InMemoryEventBus>>();
            var middlewares = sp.GetServices<IEventBusMiddleware>().ToArray();
            return new InMemoryEventBus(logger, maxScrollback: 1000, middlewares);
        });
        builder.Services.AddSingleton<ISystemPromptBuilder>(sp => new SystemPromptBuilder(sp.GetRequiredService<ILogger<SystemPromptBuilder>>()));
        builder.Services.AddSingleton<MessageConverter>();
        builder.Services.AddSingleton<IRetryPolicy, RetryPolicy>();
        builder.Services.AddSingleton<IAgentLoop, AgentLoop>();
        builder.Services.AddSingleton<IAgent, DefaultAgent>();

        builder.Services.AddSingleton<IAppConfigStore<CliConfig>>(sp =>
            new JsonAppConfigStore<CliConfig>(
                new CliConfig(),
                sp.GetRequiredService<ILogger<JsonAppConfigStore<CliConfig>>>()));
        builder.Services.AddSingleton(sp =>
        {
            var store = sp.GetRequiredService<IAppConfigStore<CliConfig>>();
#pragma warning disable RS0030 // Sync-over-async at startup — no SynchronizationContext, safe to block.
            var result = store.LoadAsync().GetAwaiter().GetResult();
#pragma warning restore RS0030
            if (result.IsFailure)
            {
                _logger.LogWarning("Failed to load CliConfig, using defaults: {Error}", result.Error);
                return new CliConfig();
            }
            return result.Value;
        });

        builder.Services.AddSingleton<ICommonConfigStore>(sp =>
            new JsonCommonConfigStore(
                new CommonConfig(),
                sp.GetRequiredService<ILogger<JsonCommonConfigStore>>()));
        builder.Services.AddSingleton(sp =>
        {
            var store = sp.GetRequiredService<ICommonConfigStore>();
#pragma warning disable RS0030 // Sync-over-async at startup — no SynchronizationContext, safe to block.
            var result = store.LoadAsync().GetAwaiter().GetResult();
#pragma warning restore RS0030
            if (result.IsFailure)
            {
                _logger.LogWarning("Failed to load CommonConfig, using defaults: {Error}", result.Error);
                return new CommonConfig();
            }
            return result.Value;
        });

        builder.Services.AddSingleton<CompositeConfig<CliConfig>>(sp =>
            new CompositeConfig<CliConfig>(
                sp.GetRequiredService<CommonConfig>(),
                sp.GetRequiredService<CliConfig>()));
    }

    private static void RegisterRegistries(HostApplicationBuilder builder, string harborDir)
    {
        _logger.LogInformation("Loading config");
        var tempSp = builder.Services.BuildServiceProvider();
        var loggerFactory = tempSp.GetRequiredService<ILoggerFactory>();
        var configStore = new JsonConfigStore(logger: loggerFactory.CreateLogger<JsonConfigStore>());
#pragma warning disable RS0030 // Do not use APIs banned for analyzers — DI setup is synchronous, no SynchronizationContext present
        var config = configStore.LoadAsync().GetAwaiter().GetResult().Value;
#pragma warning restore RS0030
        ApplyEnvOverrides(config);

        _logger.LogInformation("Registering agents, tools, providers, compaction, and permissions");

        var agentRegistry = CreateAgentRegistry(config);
        var mcpRegistry = new McpRegistry(
            loggerFactory.CreateLogger<McpRegistry>());

        // Load MCP servers from the standard mcp.json files in overlay order
        // (later wins): an explicit HARBOR_MCP_CONFIG, then ~/.harbor/mcp.json,
        // then <project>/.harbor/mcp.json. ${projectRoot}/${home}/${harborHome}
        // macros are expanded; disabled servers are skipped. No new protocol is
        // introduced — each entry is just spawned as a stdio JSON-RPC process.
        string projectRoot = Directory.GetCurrentDirectory();
        string homeDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string harborHome = Path.Combine(homeDir, ".harbor");
        var mcpLoader = new McpServersConfigLoader(projectRoot, homeDir, harborHome);

        var mcpConfigPaths = new List<string>();
        string? explicitMcp = Environment.GetEnvironmentVariable("HARBOR_MCP_CONFIG");
        if (!string.IsNullOrEmpty(explicitMcp))
            mcpConfigPaths.Add(explicitMcp);
        mcpConfigPaths.Add(Path.Combine(harborHome, "mcp.json"));
        mcpConfigPaths.Add(Path.Combine(projectRoot, ".harbor", "mcp.json"));

        foreach (var entry in mcpLoader.Load(mcpConfigPaths.ToArray()))
            mcpRegistry.Register(entry.Name, entry.StartInfo);
        var toolRegistry = CreateToolRegistry(tempSp, mcpRegistry, agentRegistry);
        var providerRegistry = CreateProviderRegistry(tempSp, harborDir, config);
        var eventBus = tempSp.GetRequiredService<IEventBus>();
        var panelRegistry = new PanelRegistry(
            loggerFactory.CreateLogger<PanelRegistry>());

#if HARBOR_WITH_PLUGINS
        var pluginHost = new PluginLoadHost(
            builder.Services,
            builder.Configuration,
            loggerFactory,
            eventBus,
            toolRegistry,
            providerRegistry,
            agentRegistry,
            panelRegistry);

        string globalPluginsDir = Path.Combine(harborDir, "plugins");
        string projectPluginsDir = Path.Combine(Directory.GetCurrentDirectory(), ".harbor", "plugins");
        string pluginsCacheDir = Path.Combine(globalPluginsDir, "cache");
        var pluginReferences = new PluginAssemblyReferences(
            loggerFactory.CreateLogger<PluginAssemblyReferences>());

        var pluginRuntime = new PluginHostBuilder()
            .WithSource(new FileSystemPluginSource(
                new[] { globalPluginsDir, projectPluginsDir },
                loggerFactory.CreateLogger<FileSystemPluginSource>()))
            .WithCompiler(new CachingCompiler(
                new RoslynPluginCompiler(pluginReferences),
                pluginsCacheDir,
                loggerFactory.CreateLogger<CachingCompiler>()))
            .WithInstantiator(new ReflectionPluginInstantiator())
            .WithRegistrar(new SafePluginRegistrar(
                new PluginRegistrar(globalPluginsDir, loggerFactory.CreateLogger<PluginRegistrar>(), loggerFactory),
                loggerFactory.CreateLogger<SafePluginRegistrar>()))
            .WithOptions(o => o.PluginRoot = globalPluginsDir)
            .Build(loggerFactory.CreateLogger<PluginHost>());
#pragma warning disable RS0030 // Sync-over-async at startup — same pattern as config load above.
        var pluginResult = pluginRuntime.LoadAllAsync(pluginHost).GetAwaiter().GetResult();
#pragma warning restore RS0030
        if (pluginResult.IsSuccess)
        {
            _logger.LogInformation("Loaded {Count} CS plugin(s)", pluginResult.Value.Count);
            foreach (var p in pluginResult.Value)
            {
                _logger.LogInformation("  - {DisplayName} (from cache: {FromCache})", p.DisplayName, p.LoadedFromCache);
            }
        }
        else
        {
            _logger.LogWarning("CS plugin loading failed: {Error}", pluginResult.Error);
        }
#else
        _logger.LogInformation("Plugin runtime disabled (HarborWithPlugins=false)");
#endif

        toolRegistry.Freeze();
        providerRegistry.Freeze();

        builder.Services.AddSingleton<IAgentRegistry>(agentRegistry);
        builder.Services.AddSingleton<IToolRegistry>(toolRegistry);
        builder.Services.AddSingleton<IProviderRegistry>(providerRegistry);
        builder.Services.AddSingleton<IEventBus>(eventBus);
        builder.Services.AddSingleton<IMcpRegistry>(mcpRegistry);
        builder.Services.AddSingleton(panelRegistry);
        builder.Services.AddSingleton<IPanelRegistry>(panelRegistry);
        builder.Services.AddSingleton<ICompactionService>(sp => new CompactionService(
            sp.GetRequiredService<ITokenTracker>(),
            sp.GetRequiredService<IProviderRegistry>(),
            sp.GetRequiredService<ILogger<CompactionService>>(),
            config.SecondaryModel));
        builder.Services.AddSingleton<IPermissionService>(sp => new PermissionService(
            sp.GetRequiredService<IAgentRegistry>(),
            sp.GetRequiredService<ILogger<PermissionService>>(),
            workspaceRoot: Directory.GetCurrentDirectory()));
    }

    private static void ApplyEnvOverrides(HarborConfig config)
    {
        string? envModel = Environment.GetEnvironmentVariable("HARBOR_MODEL");
        if (!string.IsNullOrEmpty(envModel))
        {
            config.Model = envModel;
        }
    }

    private static AgentRegistry CreateAgentRegistry(HarborConfig config)
    {
        var registry = new AgentRegistry();
        var ab = new AgentRegistryBuilder(registry);
        string[] parts = config.EffectiveModel.Split('/', 2);
        string providerId = parts[0];
        string modelId = parts.Length > 1 ? parts[1] : config.Model;
        ab.AddAgent(AgentDefinition.CodeDefault(modelId, providerId));
        ab.AddAgent(AgentDefinition.PlanDefault(modelId, providerId));
        ab.AddAgent(AgentDefinition.ExploreDefault(modelId, providerId));
        return registry;
    }

    private static void RegisterStorage(HostApplicationBuilder builder, string sessionsDir, string sqlitePath)
    {
        var tempSp = builder.Services.BuildServiceProvider();
        var commonConfig = tempSp.GetRequiredService<CommonConfig>();
        string defaultStorage = string.IsNullOrEmpty(commonConfig.StorageBackend) ? "jsonl" : commonConfig.StorageBackend;
        string storage = Environment.GetEnvironmentVariable("HARBOR_STORAGE") ?? defaultStorage;
        _logger.LogInformation("Storage backend: {Storage}", storage);
        builder.Services.AddSingleton<ISessionStore>(sp => storage.ToLowerInvariant() switch
        {
            "memory" => new MemorySessionStore(),
#if HARBOR_WITH_ALL_PROVIDERS
            "sqlite" => new SqliteSessionStore(sqlitePath, sp.GetRequiredService<ILogger<SqliteSessionStore>>()),
#endif
            _ => new JsonlSessionStore(sessionsDir, sp.GetRequiredService<ILogger<JsonlSessionStore>>())
        });
    }

    private static void RegisterTui(HostApplicationBuilder builder)
    {
        var tempSp = builder.Services.BuildServiceProvider();
        var cliConfig = tempSp.GetRequiredService<CliConfig>();
        string defaultTui = string.IsNullOrEmpty(cliConfig.DefaultTuiRenderer) || cliConfig.DefaultTuiRenderer == "auto"
            ? "spectre-tui"
            : cliConfig.DefaultTuiRenderer;
#if HARBOR_WITH_SPECTRE_TUI
        string tui = Environment.GetEnvironmentVariable("HARBOR_TUI") ?? defaultTui;
        _logger.LogInformation("TUI renderer: {Tui}", tui);
#else
        const string tui = "plain";
        _logger.LogInformation("TUI renderer: {Tui} (HARBOR_WITH_SPECTRE_TUI forces plain)", tui);
#endif
        builder.Services.AddSingleton<ITuiRenderer>(sp =>
        {
#if HARBOR_WITH_SPECTRE_TUI
            return tui.ToLowerInvariant() switch
            {
                "plain" => new PlainTuiRenderer(),
                "spectre" => new SpectreTuiRenderer(sp.GetRequiredService<ILogger<SpectreTuiRenderer>>()),
                "fullscreen" => new FullscreenTuiRenderer(sp.GetRequiredService<ILogger<FullscreenTuiRenderer>>()),
                "spectre-tui" => new Tui.SpectreTui.SpectreTuiRenderer(
                    sp.GetRequiredService<ILogger<Tui.SpectreTui.SpectreTuiRenderer>>(),
                    sp.GetService<PanelRegistry>()),
                "terminal-gui" => new TerminalGuiRenderer(sp.GetRequiredService<ILogger<TerminalGuiRenderer>>()),
                "termina" => new TerminaRenderer(sp.GetRequiredService<ILogger<TerminaRenderer>>()),
                "razor" => new RazorConsoleRenderer(sp.GetRequiredService<ILogger<RazorConsoleRenderer>>()),
                _ => new AnsiTuiRenderer(sp.GetRequiredService<ILogger<AnsiTuiRenderer>>())
            };
#else
            return new PlainTuiRenderer();
#endif
        });
    }

    private static void RegisterIpc(HostApplicationBuilder builder)
    {
        string mode = Environment.GetEnvironmentVariable("HARBOR_MODE") ?? "inprocess";
        _logger.LogInformation("HARBOR_MODE = {Mode}", mode);

        string pipeName = Environment.GetEnvironmentVariable("HARBOR_IPC_PIPE") ?? "harbor-ipc";

        switch (mode.ToLowerInvariant())
        {
            case "inprocess":
                builder.Services.UseInProcessHarborClient();
                break;
            case "ipc-server":
                builder.Services.UseInProcessHarborClient();
                builder.Services.UseHarborIpcServer(pipeName);
                break;
            case "ipc-client":
                builder.Services.UseIpcHarborClient(pipeName);
                break;
            default:
                throw new ArgumentException(
                    $"Unknown HARBOR_MODE: '{mode}'. Expected one of: inprocess, ipc-server, ipc-client.");
        }
    }
}
