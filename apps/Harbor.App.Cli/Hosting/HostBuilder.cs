using Harbor.Abstractions.Agents;
using Harbor.Abstractions.Events;
using Harbor.Core.Events;
using Harbor.Abstractions.Permissions;
using Harbor.Abstractions.Providers;
using Harbor.Abstractions.Sessions;
using Harbor.Abstractions.Tools;
using Harbor.Cli.Configuration;
using Harbor.Cli.Logging;
using Harbor.Core.Agents;
using Harbor.Core.Configuration;
using Harbor.Core.Onboarding;
using Harbor.Core.Permissions;
using Harbor.Core.Sessions;
using Harbor.Core.Tools;
using Harbor.Desktop.Abstractions.Configuration;
using Harbor.Ipc.Client;
using Harbor.Ipc.InProcess;
using Harbor.Ipc.Server;
using Harbor.Providers.Ollama;
using Harbor.Storage.Jsonl;
using Harbor.Storage.Memory;
using Harbor.Terminal.Abstractions;
using Harbor.Tools.Builtin;
using Harbor.Tui.Plain;
using Harbor.Ui.Framework.Diagnostics;
using Harbor.Ui.Framework.Panels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
#if HARBOR_WITH_PLUGINS
using Harbor.Plugins.Compilation;
using Harbor.Plugins.Hosting;
using Harbor.Plugins.Instantiation;
using Harbor.Plugins.Registration;
using Harbor.Plugins.Storage;
#endif
#if HARBOR_WITH_ALL_PROVIDERS
using Harbor.Providers.Anthropic;
using Harbor.Providers.OpenAI;
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
#if HARBOR_WITH_PLUGINS
using Excubo.Analyzers.DependencyInjection;
#endif
namespace Harbor.Cli.Hosting;
/// <summary>
///     DI host configuration — single responsibility: wire services.
///     Extracted from Program.cs to reduce god object.
/// </summary>
internal static class HostBuilder
{
    private static ILoggerFactory _loggerFactory = null!;
    private static ILogger _logger = null!;

    /// <summary>
    ///     Runtime probe: returns true if the named assembly has been loaded
    ///     into the current AppDomain. Used as defense-in-depth to skip
    ///     service registration for optional features whose ProjectReference
    ///     has been excluded at build time (via the HarborWith* MSBuild flags).
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         This is the runtime safety net on top of the compile-time
    ///         <c>#if HARBOR_WITH_*</c> guards. It catches the case where a
    ///         plugin/scripting assembly is referenced but never loaded (e.g.
    ///         if the type isn't directly used during startup, the CLR doesn't
    ///         eagerly load its assembly).
    ///     </para>
    /// </remarks>
    private static bool IsAssemblyLoaded(string name) =>
        AppDomain.CurrentDomain.GetAssemblies()
            .Any(a => string.Equals(a.GetName().Name, name, StringComparison.OrdinalIgnoreCase));

    // Each [Exposes(typeof(T))] declaration below is enforced by
    // Excubo.Analyzers.DependencyInjectionValidation (rules EDI01–EDI04) and
    // exercised by Harbor.App.Cli.Tests/HostBuilderDiTests.cs which builds the
    // host and asserts every [Exposes] type is resolvable from the resulting
    // IServiceProvider. Keep this list in sync with the actual services.AddXxx
    // calls in RegisterCore / RegisterRegistries / RegisterStorage / RegisterTui.
    //
    // Note: the [Exposes] attributes are emitted only when Excubo is
    // referenced (i.e. when at least one optional ProjectReference that
    // brings it transitively is included — currently Harbor.Plugins.*).
    // The DI tests still resolve these services in all build variants.
#if HARBOR_WITH_PLUGINS
    [Exposes(typeof(IConfigStore))]
    [Exposes(typeof(AuthStore))]
    [Exposes(typeof(OnboardingWizard))]
    [Exposes(typeof(ITokenEstimator))]
    [Exposes(typeof(IEventBus))]
    [Exposes(typeof(ISystemPromptBuilder))]
    [Exposes(typeof(MessageConverter))]
    [Exposes(typeof(IAgentLoop))]
    [Exposes(typeof(IAgent))]
    [Exposes(typeof(IAgentRegistry))]
    [Exposes(typeof(IToolRegistry))]
    [Exposes(typeof(IProviderRegistry))]
    [Exposes(typeof(IMcpRegistry))]
    [Exposes(typeof(PanelRegistry))]
    [Exposes(typeof(IPanelRegistry))]
    [Exposes(typeof(ICompactionService))]
    [Exposes(typeof(IPermissionService))]
    [Exposes(typeof(ISessionStore))]
    [Exposes(typeof(ITuiRenderer))]
    [Exposes(typeof(IAppConfigStore<CliConfig>))]
    [Exposes(typeof(CliConfig))]
    [Exposes(typeof(ICommonConfigStore))]
    [Exposes(typeof(CommonConfig))]
    [Exposes(typeof(CompositeConfig<CliConfig>))]
#endif
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
        _logger.LogInformation("Feature flags: plugins={Plugins}, scripting={Scripting}, " +
                               "spectre-tui={SpectreTui}, all-providers={AllProviders}",
            IsAssemblyLoaded("Harbor.Plugins.Runtime"),
            IsAssemblyLoaded("Harbor.Scripting.Hosting"),
            IsAssemblyLoaded("Harbor.Tui.Spectre"),
            IsAssemblyLoaded("Harbor.Providers.Anthropic"));

        RegisterCore(builder);
        // HTTP clients must be registered BEFORE RegisterRegistries because
        // CreateProviderRegistry resolves IHttpClientFactory from the temporary
        // ServiceProvider to wire named clients (anthropic, openai, ollama).
        // Registering them after would throw at host-build time — caught by
        // Harbor.App.Cli.Tests/HostBuilderDiTests.cs.
        RegisterHttpClients(builder);
        RegisterRegistries(builder, harborDir);
        RegisterStorage(builder, sessionsDir, sqlitePath);
        RegisterTui(builder);
        RegisterIpc(builder);
        return builder.Build();
    }

    /// <summary>
    ///     Register the IPC layer (IHarborClient + optional IHarborServer)
    ///     based on the HARBOR_MODE env var.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>HARBOR_MODE values:</b>
    ///     </para>
    ///     <list type="bullet">
    ///         <item><c>inprocess</c> (default) — InProcessHarborClient calls IAgent/ISessionStore/etc. directly.</item>
    ///         <item><c>ipc-server</c> — InProcessHarborClient + HarborIpcServer (accepts remote clients).</item>
    ///         <item><c>ipc-client</c> — IpcHarborClient only (thin, talks to a running ipc-server).</item>
    ///     </list>
    ///     <para>
    ///         For <c>ipc-client</c> mode, the application-layer services
    ///         (IAgent, ISessionStore, IProviderRegistry, ...) are still
    ///         registered by RegisterCore / RegisterRegistries — but the
    ///         IpcHarborClient does not use them. A future optimization could
    ///         skip their registration entirely in this mode.
    ///     </para>
    /// </remarks>
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

    private static void ConfigureLogging(HostApplicationBuilder builder, string[] args)
    {
        builder.Logging.ClearProviders();
        var logLevel = Program.ResolveLogLevel(args);

        // Interactive TUI detection: same logic as Program.Main. When an
        // interactive TUI is active (SpectreTUI / Termina / Terminal.Gui /
        // RazorConsole / Fullscreen / Spectre), the alt-screen buffer is owned
        // by the TUI and any Console.Write from the simple-console logger
        // corrupts the rendered frame. We:
        //   * skip AddSimpleConsole (no Console.Out writes from ILogger),
        //   * attach the shared IDiagnosticsPanel via DiagnosticsPanelLoggerProvider
        //     so log entries flow into the in-TUI F12 panel,
        //   * register the IDiagnosticsPanel singleton in DI so renderers can
        //     resolve it from PanelContext.Services / IServiceProvider.
        // File logging stays on regardless (HarborLogManager.Current is added
        // by Program.Main and we re-attach the same instance here).
        bool interactiveTui = TuiMode.WillEnterInteractiveTui(args);

        // Re-attach the shared file logger so host-build / runtime logging lands
        // in the same per-run file as Program.Main's pre-host logging.
        var fileProvider = HarborLogManager.Current;
        if (fileProvider is not null)
            builder.Logging.AddProvider(fileProvider);

        if (interactiveTui)
        {
            var panel = DiagnosticsSink.Initialize();
            builder.Logging.AddProvider(new DiagnosticsPanelLoggerProvider(panel));
            // Register the same singleton in DI so renderers can resolve it
            // (PanelContext.Services.GetService<IDiagnosticsPanel>()).
            builder.Services.AddSingleton<IDiagnosticsPanel>(panel);
        }
        else if (logLevel <= LogLevel.Information)
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
        // SamplingMiddleware (rate=0.1) drops ~90% of MessageUpdateEvents which
        // breaks streaming text delivery to TUI renderers — not registered.
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
        builder.Services.AddSingleton<IAgentLoop, AgentLoop>();
        builder.Services.AddSingleton<IAgent, DefaultAgent>();

        // ── Per-app CLI configuration (~/.harbor/cli.json) ──
        // Registered early so RegisterTui / RegisterStorage can resolve CliConfig
        // synchronously from the temp ServiceProvider. JsonAppConfigStore uses a
        // SemaphoreSlim for thread-safe atomic writes; LoadAsync falls back to
        // the default CliConfig() when the file is missing.
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

        // ── Shared common configuration (~/.harbor/config.json) ──
        // CommonConfig holds API keys, default provider/model, storage backend,
        // log level, permissions, plugins, network, compaction — every field
        // that is shared across ALL Harbor apps (CLI, Avalonia, WPF, MAUI,
        // Blazor). Loaded eagerly so RegisterStorage / RegisterRegistries can
        // resolve CommonConfig synchronously from the temp ServiceProvider.
        // Same atomic-write + thread-safe pattern as JsonAppConfigStore<T>.
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

        // ── Composite: CommonConfig + CliConfig ──
        // Convenience pair so services that need fields from BOTH layers can
        // take a single dependency instead of two. Resolved after both
        // singletons above so the factory can build a snapshot.
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

        // Construct registries eagerly so CS plugins can extend them BEFORE builder.Build().
        // The instances are then registered as singletons so the final ServiceProvider uses
        // the same registry objects the plugin loader wrote into.
        var agentRegistry = CreateAgentRegistry(config);
        // MCP registry is constructed eagerly so the builtin `mcp` tool can be wired with
        // a concrete registry reference at tool-registration time (ToolContext.Services is
        // not populated by the default AgentLoop, so we can't rely on late resolution).
        var mcpRegistry = new InMemoryMcpRegistry(
            loggerFactory.CreateLogger<InMemoryMcpRegistry>());
        // Pass agentRegistry directly to CreateToolRegistry — it isn't registered in
        // builder.Services until AFTER this call (line ~252), so resolving via
        // tempSp.GetRequiredService<IAgentRegistry>() would throw. The DI test
        // (HostBuilderDiTests.Build_Registers_ISessionStore) caught this.
        var toolRegistry = CreateToolRegistry(tempSp, mcpRegistry, agentRegistry);
        var providerRegistry = CreateProviderRegistry(tempSp, harborDir, config);
        var eventBus = tempSp.GetRequiredService<IEventBus>();
        // The host-owned PanelRegistry. Plugin-contributed IPanelProviders land here
        // via IPluginLoadHost.RegisterPanelProvider; the SpectreTUI renderer resolves
        // the same singleton from DI when its interactive loop starts.
        var panelRegistry = new PanelRegistry(
            loggerFactory.CreateLogger<PanelRegistry>());

#if HARBOR_WITH_PLUGINS
        // Run the CS-source plugin loader. Adds tools / providers / agents / TUI plugins
        // contributed via Roslyn-compiled .cs files in ~/.harbor/plugins/ or
        // <cwd>/.harbor/plugins/.
        //
        // Composed from the four layers: storage (FileSystemPluginSource) →
        // compilation (CachingCompiler over RoslynPluginCompiler) → instantiation
        // (ReflectionPluginInstantiator) → registration (SafePluginRegistrar over
        // PluginRegistrar). Each layer is independently swappable.
        //
        // EXCLUDED when HarborWithPlugins=false — the entire Harbor.Plugins.* stack
        // is removed from the project reference graph, so the plugin host can't be
        // constructed. See apps/Harbor.App.Cli/Harbor.App.Cli.csproj.
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
#else
        _logger.LogInformation("Plugin runtime disabled (HarborWithPlugins=false)");
#endif

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
            // Model setter parses "provider/model"; the provider component is
            // derived from it via IdentityConfig.EffectiveProvider.
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

    private static ToolRegistry CreateToolRegistry(IServiceProvider sp, IMcpRegistry mcpRegistry, IAgentRegistry agentRegistry)
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
        // agentRegistry is passed in directly (see call site) because the DI
        // registration happens after this method returns.
        tb.AddTool(new TaskTool(agentRegistry, loggerFactory.CreateLogger<TaskTool>()));

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

        // Ollama is always available (kept in minimal builds).
        pb.AddProvider("ollama", () => new OllamaLlmClient(
            httpFactory.CreateClient("ollama"), new OllamaConfig(),
            loggerFactory.CreateLogger<OllamaLlmClient>()));

#if HARBOR_WITH_ALL_PROVIDERS
        // Native providers — Anthropic + OpenAI excluded from minimal builds.
        pb.AddProvider("anthropic", () => new AnthropicLlmClient(
            httpFactory.CreateClient("anthropic"), new AnthropicConfig(),
            new ConfigAuthResolver(authStore, "anthropic"),
            loggerFactory.CreateLogger<AnthropicLlmClient>()));
        pb.AddProvider("openai", () => new OpenAILlmClient(
            httpFactory.CreateClient("openai"), new OpenAIConfig(),
            new ConfigAuthResolver(authStore, "openai"),
            loggerFactory.CreateLogger<OpenAILlmClient>()));

        // JSON + embedded OpenAI-compatible providers — excluded from minimal builds
        // (Pulls in Harbor.Providers.OpenAiCompatible types).
        ProviderRegistration.RegisterJsonProviders(pb, httpFactory, loggerFactory, cacheDir, authStore);
#endif

        registry.Freeze();
        _logger.LogInformation("Registered providers: {Count}", registry.GetRegisteredProviderIds().Count);
        return registry;
    }

    private static void RegisterStorage(HostApplicationBuilder builder, string sessionsDir, string sqlitePath)
    {
        // Resolve CommonConfig from a temp ServiceProvider so we can read the
        // shared StorageBackend preference. The HARBOR_STORAGE env var still
        // wins (matches the legacy HarborConfig behavior — env vars override
        // persisted config).
        //
        // NOTE: storage used to live on CliConfig.DefaultStorage (B2 layout).
        // As of task C1 it lives on CommonConfig.StorageBackend so the user's
        // choice is shared across every Harbor app.
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
        // Resolve CliConfig from a temp ServiceProvider so we can read the
        // per-app DefaultTuiRenderer preference. The HARBOR_TUI env var still
        // wins. The literal "auto" is resolved to "spectre-tui" here (the
        // CLI's default for interactive sessions).
        var tempSp = builder.Services.BuildServiceProvider();
        var cliConfig = tempSp.GetRequiredService<CliConfig>();
        string defaultTui = string.IsNullOrEmpty(cliConfig.DefaultTuiRenderer) || cliConfig.DefaultTuiRenderer == "auto"
            ? "spectre-tui"
            : cliConfig.DefaultTuiRenderer;
#if HARBOR_WITH_SPECTRE_TUI
        string tui = Environment.GetEnvironmentVariable("HARBOR_TUI") ?? defaultTui;
        _logger.LogInformation("TUI renderer: {Tui}", tui);
#else
        // Minimal / no-Spectre builds ship only PlainTuiRenderer; force the default to "plain".
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

                // AnsiTuiRenderer is the only non-Plain fallback in full builds.
                _ => new AnsiTuiRenderer(sp.GetRequiredService<ILogger<AnsiTuiRenderer>>())
            };
#else
            // Minimal / no-Spectre builds ship PlainTuiRenderer only.
            return new PlainTuiRenderer();
#endif
        });
    }

    private static void RegisterHttpClients(HostApplicationBuilder builder)
    {
        _logger.LogInformation("Registering HTTP clients");
        builder.Services.AddHttpClient("ollama");
#if HARBOR_WITH_ALL_PROVIDERS
        builder.Services.AddHttpClient("anthropic");
        builder.Services.AddHttpClient("openai");
        builder.Services.AddHttpClient("providers");
        builder.Services.AddHttpClient("default");
#else
        _logger.LogInformation("HARBOR_WITH_ALL_PROVIDERS=false — registered only the ollama HTTP client");
#endif
    }
}
