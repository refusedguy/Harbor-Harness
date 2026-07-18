using Harbor.Plugins.Abstractions;
using Harbor.Abstractions.Agents;
using Harbor.Abstractions.Events;
using Harbor.Abstractions.Permissions;
using Harbor.Abstractions.Providers;
using Harbor.Abstractions.Sessions;
using Harbor.Abstractions.Tools;
using Harbor.Core.Agents;
using Harbor.Core.Configuration;
using Harbor.Core.Onboarding;
using Harbor.Core.Permissions;
using Harbor.Core.Sessions;
using Harbor.Core.Tools;
using Harbor.Plugins.Runtime;
using Harbor.Plugins.Compilation;
using Harbor.Plugins.Hosting;
using Harbor.Plugins.Instantiation;
using Harbor.Plugins.Registration;
using Harbor.Plugins.Storage;
using Harbor.Providers.Anthropic;
using Harbor.Providers.Ollama;
using Harbor.Providers.OpenAI;
using Harbor.Storage.Jsonl;
using Harbor.Storage.Memory;
using Harbor.Storage.Sqlite;
using Harbor.Tools.Builtin;
using Harbor.Tui.Abstractions;
using Harbor.Tui.Abstractions.Panels;
using Harbor.Tui.Ansi;
using Harbor.Tui.Plain;
using Harbor.Tui.RazorConsole;
using Harbor.Tui.Spectre;
using Harbor.Tui.Spectre.Fullscreen;
using Harbor.Tui.Termina;
using Harbor.Tui.TerminalGui;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
namespace Harbor.Cli.Hosting;
/// <summary>
///     DI host configuration — single responsibility: wire services.
///     Extracted from Program.cs to reduce god object.
/// </summary>
internal static class HostBuilder
{
    private static ILoggerFactory _loggerFactory = null!;
    private static ILogger _logger = null!;

    public static IHost Build(params string[] args)
    {
        string homeDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string harborDir = Path.Combine(homeDir, ".harbor");
        string sessionsDir = Path.Combine(harborDir, "sessions");
        string cacheDir = Path.Combine(harborDir, "cache");
        string sqlitePath = Path.Combine(harborDir, "sessions.db");

        Directory.CreateDirectory(harborDir);
        Directory.CreateDirectory(sessionsDir);
        Directory.CreateDirectory(cacheDir);

        var builder = Host.CreateApplicationBuilder();
        ConfigureLogging(builder, args);

        _loggerFactory = builder.Services.BuildServiceProvider().GetRequiredService<ILoggerFactory>();
        _logger = _loggerFactory.CreateLogger(typeof(HostBuilder).FullName ?? "HostBuilder");

        _logger.LogInformation("Building host");
        RegisterCore(builder);
        RegisterRegistries(builder, harborDir);
        RegisterStorage(builder, sessionsDir, sqlitePath);
        RegisterTui(builder);
        RegisterHttpClients(builder);
        return builder.Build();
    }

    private static void ConfigureLogging(HostApplicationBuilder builder, string[] args)
    {
        builder.Logging.ClearProviders();
        var logLevel = Program.ResolveLogLevel(args);

        if (logLevel <= LogLevel.Information)
        {
            builder.Logging.AddSimpleConsole(o =>
            {
                o.SingleLine = true;
                o.TimestampFormat = "HH:mm:ss ";
            });
        }
        builder.Logging.SetMinimumLevel(logLevel);
    }

    private static void RegisterCore(HostApplicationBuilder builder)
    {
        _logger.LogInformation("Registering core services");
        builder.Services.AddSingleton<IConfigStore>(sp => new JsonConfigStore(
            logger: sp.GetRequiredService<ILogger<JsonConfigStore>>()));
        builder.Services.AddSingleton<AuthStore>();
        builder.Services.AddSingleton<OnboardingWizard>();
        builder.Services.AddSingleton<ITokenEstimator, HeuristicTokenEstimator>();
        builder.Services.AddSingleton<IEventBus>(sp => new InMemoryEventBus(sp.GetRequiredService<ILogger<InMemoryEventBus>>()));
        builder.Services.AddSingleton<ISystemPromptBuilder>(sp => new SystemPromptBuilder(sp.GetRequiredService<ILogger<SystemPromptBuilder>>()));
        builder.Services.AddSingleton<MessageConverter>();
        builder.Services.AddSingleton<IAgentLoop, AgentLoop>();
        builder.Services.AddSingleton<IAgent, DefaultAgent>();
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

        // Construct registries eagerly so CS plugins can extend them BEFORE builder.Build().
        // The instances are then registered as singletons so the final ServiceProvider uses
        // the same registry objects the plugin loader wrote into.
        var agentRegistry = CreateAgentRegistry(config);
        // MCP registry is constructed eagerly so the builtin `mcp` tool can be wired with
        // a concrete registry reference at tool-registration time (ToolContext.Services is
        // not populated by the default AgentLoop, so we can't rely on late resolution).
        var mcpRegistry = new InMemoryMcpRegistry(
            loggerFactory.CreateLogger<InMemoryMcpRegistry>());
        var toolRegistry = CreateToolRegistry(tempSp, mcpRegistry);
        var providerRegistry = CreateProviderRegistry(tempSp, harborDir, config);
        var eventBus = tempSp.GetRequiredService<IEventBus>();
        // The host-owned PanelRegistry. Plugin-contributed IPanelProviders land here
        // via IPluginLoadHost.RegisterPanelProvider; the SpectreTUI renderer resolves
        // the same singleton from DI when its interactive loop starts.
        var panelRegistry = new PanelRegistry(
            loggerFactory.CreateLogger<PanelRegistry>());

        // Run the CS-source plugin loader. Adds tools / providers / agents / TUI plugins
        // contributed via Roslyn-compiled .cs files in ~/.harbor/plugins/ or
        // <cwd>/.harbor/plugins/.
        //
        // Composed from the four layers: storage (FileSystemPluginSource) →
        // compilation (CachingCompiler over RoslynPluginCompiler) → instantiation
        // (ReflectionPluginInstantiator) → registration (SafePluginRegistrar over
        // PluginRegistrar). Each layer is independently swappable.
        var pluginHost = new PluginLoadHost(
            services: builder.Services,
            configuration: builder.Configuration,
            loggerFactory: loggerFactory,
            eventBus: eventBus,
            tools: toolRegistry,
            providers: providerRegistry,
            agents: agentRegistry,
            panels: panelRegistry);

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
                new PluginRegistrar(globalPluginsDir, loggerFactory.CreateLogger<PluginRegistrar>()),
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

        // Re-freeze the registries so post-Build() lookups hit the frozen O(1) snapshot.
        // Plugins added entries via Register which invalidated the previous snapshot.
        toolRegistry.Freeze();
        providerRegistry.Freeze();

        // Register the already-constructed instances as singletons. The previous factory
        // descriptors remain in the ServiceCollection but the instance descriptors win.
        builder.Services.AddSingleton<IAgentRegistry>(agentRegistry);
        builder.Services.AddSingleton<IToolRegistry>(toolRegistry);
        builder.Services.AddSingleton<IProviderRegistry>(providerRegistry);
        builder.Services.AddSingleton<IEventBus>(eventBus);
        builder.Services.AddSingleton<IMcpRegistry>(mcpRegistry);
        builder.Services.AddSingleton(panelRegistry);
        builder.Services.AddSingleton<IPanelRegistry>(panelRegistry);
        builder.Services.AddSingleton<ICompactionService>(sp => new CompactionService(
            sp.GetRequiredService<ITokenEstimator>(),
            sp.GetRequiredService<IProviderRegistry>(),
            sp.GetRequiredService<ILogger<CompactionService>>()));
        builder.Services.AddSingleton<IPermissionService>(sp => new PermissionService(
            sp.GetRequiredService<IAgentRegistry>(),
            sp.GetRequiredService<ILogger<PermissionService>>()));
    }

    private static void ApplyEnvOverrides(HarborConfig config)
    {
        string? envModel = Environment.GetEnvironmentVariable("HARBOR_MODEL");
        if (!string.IsNullOrEmpty(envModel))
        {
            config.Model = envModel;
            string[] parts = envModel.Split('/', 2);
            config.Provider = parts[0];
        }
    }

    private static AgentRegistry CreateAgentRegistry(HarborConfig config)
    {
        var registry = new AgentRegistry();
        var ab = new AgentRegistryBuilder(registry);
        string[] parts = config.Model.Split('/', 2);
        string providerId = parts[0];
        string modelId = parts.Length > 1 ? parts[1] : config.Model;
        ab.AddAgent(AgentDefinition.CodeDefault(modelId, providerId));
        ab.AddAgent(AgentDefinition.PlanDefault(modelId, providerId));
        ab.AddAgent(AgentDefinition.ExploreDefault(modelId, providerId));
        return registry;
    }

    private static ToolRegistry CreateToolRegistry(IServiceProvider sp, IMcpRegistry mcpRegistry)
    {
        var registry = new ToolRegistry();
        var tb = new ToolRegistryBuilder(registry);
        var loggerFactory = sp.GetRequiredService<ILoggerFactory>();
        tb.AddTool(() => new ReadTool(loggerFactory.CreateLogger<ReadTool>()));
        tb.AddTool(() => new WriteTool(loggerFactory.CreateLogger<WriteTool>()));
        tb.AddTool(() => new EditTool(loggerFactory.CreateLogger<EditTool>()));
        tb.AddTool(() => new BashTool(loggerFactory.CreateLogger<BashTool>()));
        tb.AddTool(() => new GlobTool(loggerFactory.CreateLogger<GlobTool>()));
        tb.AddTool(() => new GrepTool(loggerFactory.CreateLogger<GrepTool>()));
        tb.AddTool(() => new LsTool(loggerFactory.CreateLogger<LsTool>()));
        tb.AddTool(new TaskTool(sp.GetRequiredService<IAgentRegistry>(), loggerFactory.CreateLogger<TaskTool>()));

        // ── Extended builtin tools (see docs/TOOLS_CATALOG.md) ──
        // WebFetch: parallel HTTP fetch + HTML→markdown conversion (no deps).
        tb.AddTool(() => new WebFetchTool(loggerFactory.CreateLogger<WebFetchTool>()));
        // Patch: unified-diff applier, atomic write, context-validated.
        tb.AddTool(() => new PatchTool(loggerFactory.CreateLogger<PatchTool>()));
        // Notebook: per-session persistent markdown notes (JSON file under ~/.harbor/notes).
        tb.AddTool(() => new NotebookTool(loggerFactory.CreateLogger<NotebookTool>()));
        // RipGrep: thin wrapper over the `rg` binary; falls back to an error if rg missing.
        tb.AddTool(() => new RipGrepTool(loggerFactory.CreateLogger<RipGrepTool>()));
        // Tree: ASCII directory tree, respects .gitignore when git is available.
        tb.AddTool(() => new TreeTool(loggerFactory.CreateLogger<TreeTool>()));
        // Mcp: bridge to MCP servers via the registry; wired eagerly so it works even when
        // ToolContext.Services is null (default AgentLoop does not populate it).
        tb.AddTool(new McpToolTool(mcpRegistry, loggerFactory.CreateLogger<McpToolTool>()));

        registry.Freeze();
        const int toolCount = 14;
        _logger.LogInformation("Registered {Count} tools", toolCount);
        return registry;
    }

    private static ProviderRegistry CreateProviderRegistry(IServiceProvider sp, string harborDir, HarborConfig config)
    {
        var registry = new ProviderRegistry(sp.GetRequiredService<ILogger<ProviderRegistry>>());
        var pb = new ProviderRegistryBuilder(registry);
        var httpFactory = sp.GetRequiredService<IHttpClientFactory>();
        var loggerFactory = sp.GetRequiredService<ILoggerFactory>();
        var authStore = sp.GetRequiredService<AuthStore>();
        string cacheDir = Path.Combine(harborDir, "cache", "providers");

        // Native providers
        pb.AddProvider("anthropic", () => new AnthropicLlmClient(
            httpFactory.CreateClient("anthropic"), new AnthropicConfig(),
            new ConfigAuthResolver(authStore, "anthropic"),
            loggerFactory.CreateLogger<AnthropicLlmClient>()));
        pb.AddProvider("openai", () => new OpenAILlmClient(
            httpFactory.CreateClient("openai"), new OpenAIConfig(),
            new ConfigAuthResolver(authStore, "openai"),
            loggerFactory.CreateLogger<OpenAILlmClient>()));
        pb.AddProvider("ollama", () => new OllamaLlmClient(
            httpFactory.CreateClient("ollama"), new OllamaConfig(),
            loggerFactory.CreateLogger<OllamaLlmClient>()));

        // JSON + embedded providers
        ProviderRegistration.RegisterJsonProviders(pb, httpFactory, loggerFactory, cacheDir, authStore);

        registry.Freeze();
        _logger.LogInformation("Registered providers: {Count}", registry.GetRegisteredProviderIds().Count);
        return registry;
    }

    private static void RegisterStorage(HostApplicationBuilder builder, string sessionsDir, string sqlitePath)
    {
        string storage = Environment.GetEnvironmentVariable("HARBOR_STORAGE") ?? "jsonl";
        _logger.LogInformation("Storage backend: {Storage}", storage);
        builder.Services.AddSingleton<ISessionStore>(sp => storage.ToLowerInvariant() switch
        {
            "memory" => new MemorySessionStore(),
            "sqlite" => new SqliteSessionStore(sqlitePath, sp.GetRequiredService<ILogger<SqliteSessionStore>>()),
            _ => new JsonlSessionStore(sessionsDir, sp.GetRequiredService<ILogger<JsonlSessionStore>>())
        });
    }

    private static void RegisterTui(HostApplicationBuilder builder)
    {
        var configStore = new JsonConfigStore(logger: builder.Services.BuildServiceProvider().GetRequiredService<ILogger<JsonConfigStore>>());
#pragma warning disable RS0030 // Do not use APIs banned for analyzers — DI setup is synchronous, no SynchronizationContext present
        var config = configStore.LoadAsync().GetAwaiter().GetResult().Value;
#pragma warning restore RS0030

        string tui = config.Tui ?? Environment.GetEnvironmentVariable("HARBOR_TUI") ?? "spectre-tui";
        _logger.LogInformation("TUI renderer: {Tui}", tui);
        builder.Services.AddSingleton<ITuiRenderer>(sp => tui.ToLowerInvariant() switch
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

            // ── Alternative UI renderers (see docs/ALTERNATIVE_UIS.md) ──
            // To enable, add the matching ProjectReference to Harbor.Cli.csproj and
            // (for MAUI) install the workload: `dotnet workload install maui`.
            // Then uncomment the case you need.
            //
            // "wpf"            => new Harbor.Tui.Wpf.WpfTuiRenderer(sp.GetRequiredService<ILogger<Harbor.Tui.Wpf.WpfTuiRenderer>>()),
            // "avalonia"       => new Harbor.Tui.Avalonia.AvaloniaTuiRenderer(sp.GetRequiredService<ILogger<Harbor.Tui.Avalonia.AvaloniaTuiRenderer>>()),
            // "maui"           => new Harbor.Tui.Maui.MauiTuiRenderer(sp.GetRequiredService<ILogger<Harbor.Tui.Maui.MauiTuiRenderer>>()),
            // "blazor"         => new Harbor.Tui.Blazor.BlazorTuiRenderer(sp.GetRequiredService<ILogger<Harbor.Tui.Blazor.BlazorTuiRenderer>>()),
            // "sixel"          => new Harbor.Tui.Sixel.SixelTuiRenderer(sp.GetRequiredService<ILogger<Harbor.Tui.Sixel.SixelTuiRenderer>>()),
            // "notifications"  => new Harbor.Tui.Notifications.NotificationTuiRenderer(sp.GetRequiredService<ILogger<Harbor.Tui.Notifications.NotificationTuiRenderer>>()),

            _ => new AnsiTuiRenderer(sp.GetRequiredService<ILogger<AnsiTuiRenderer>>())
        });
    }

    private static void RegisterHttpClients(HostApplicationBuilder builder)
    {
        _logger.LogInformation("Registering HTTP clients");
        builder.Services.AddHttpClient("anthropic");
        builder.Services.AddHttpClient("openai");
        builder.Services.AddHttpClient("ollama");
        builder.Services.AddHttpClient("providers");
        builder.Services.AddHttpClient("default");
    }
}
