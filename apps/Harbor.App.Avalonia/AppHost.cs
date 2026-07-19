using Harbor.App.Avalonia.Configuration;
using Harbor.App.Avalonia.Services;
using Harbor.App.Avalonia.ViewModels;
using Harbor.App.Avalonia.ViewModels.Shell;
using Harbor.Abstractions.Agents;
using Harbor.Abstractions.Events;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Models.Identifiers;
using Harbor.Abstractions.Permissions;
using Harbor.Abstractions.Providers;
using Harbor.Abstractions.Sessions;
using Harbor.Abstractions.Tools;
using Harbor.Core.Agents;
using Harbor.Core.Configuration;
using Harbor.Core.Permissions;
using Harbor.Core.Sessions;
using Harbor.Core.Tools;
using Harbor.Desktop.Abstractions.Configuration;
using Harbor.Ipc;
using Harbor.Ipc.Client;
using Harbor.Ipc.InProcess;
using Harbor.Providers.Ollama;
using Harbor.Providers.OpenAiCompatible;
using Harbor.Providers.OpenAiCompatible.Compat;
using Harbor.Storage.Jsonl;
using Harbor.Storage.Memory;
using Harbor.Tools.Builtin;
using Harbor.Ui.Framework.State;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Extensions.Logging;
using Excubo.Analyzers.DependencyInjection;

namespace Harbor.App.Avalonia;

/// <summary>
///     Composition root for the standalone Harbor Avalonia app. Mirrors the wiring
///     in <c>Harbor.Cli/Hosting/HostBuilder.cs</c> but trimmed to a desktop-app subset
///     (no plugins, no MCP, no JSON providers — bring-your-own-provider via Settings).
/// </summary>
internal static class AppHost
{
    /// <summary>
    ///     Build the DI host. Safe to call from Main before the Avalonia lifetime starts.
    /// </summary>
    /// <param name="args">Command-line args (forwarded to <see cref="Host.CreateApplicationBuilder"/>).</param>
    /// <returns>A started <see cref="IHost"/>. Dispose on shutdown.</returns>
    // [Exposes(typeof(T))] declarations are validated by Excubo.Analyzers.DependencyInjectionValidation
    // (EDI01–EDI04) and exercised at runtime by Harbor.App.Avalonia.Tests/AppHostDiTests.cs.
    [Exposes(typeof(ITokenEstimator))]
    [Exposes(typeof(IEventBus))]
    [Exposes(typeof(ISystemPromptBuilder))]
    [Exposes(typeof(MessageConverter))]
    [Exposes(typeof(IAgentLoop))]
    [Exposes(typeof(IAgent))]
    [Exposes(typeof(ISessionStore))]
    [Exposes(typeof(ICompactionService))]
    [Exposes(typeof(IPermissionService))]
    [Exposes(typeof(IToolRegistry))]
    [Exposes(typeof(IProviderRegistry))]
    [Exposes(typeof(IAgentRegistry))]
    [Exposes(typeof(IMcpRegistry))]
    [Exposes(typeof(UiStore))]
    [Exposes(typeof(TuiEffectHost))]
    [Exposes(typeof(ThemeService))]
    [Exposes(typeof(DialogService))]
    [Exposes(typeof(AvaloniaFilePicker))]
    [Exposes(typeof(SessionManager))]
    [Exposes(typeof(GitService))]
    [Exposes(typeof(ToastService))]
    [Exposes(typeof(AvaloniaDispatcherAdapter))]
    [Exposes(typeof(IHarborClient))]
    [Exposes(typeof(IAppConfigStore<AvaloniaConfig>))]
    [Exposes(typeof(AvaloniaConfig))]
    [Exposes(typeof(ICommonConfigStore))]
    [Exposes(typeof(CommonConfig))]
    [Exposes(typeof(CompositeConfig<AvaloniaConfig>))]
    [Exposes(typeof(OrcaShellViewModel))]
    public static async Task<IHost> BuildAsync(string[] args)
    {
        string homeDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string harborDir = Path.Combine(homeDir, ".harbor");
        string sessionsDir = Path.Combine(harborDir, "sessions");
        string sqlitePath = Path.Combine(harborDir, "sessions.db");

        Directory.CreateDirectory(harborDir);
        Directory.CreateDirectory(sessionsDir);

        var builder = Host.CreateApplicationBuilder(args);

        ConfigureLogging(builder);

        // HTTP client factory — used by OpenAI-compatible providers (Kilocode,
        // OpenRouter, DeepSeek, …). Each provider gets its own HttpClient
        // instance with a per-provider timeout. The factory handles pooling
        // + lifecycle; named clients can be added later via AddHttpClient(id).
        builder.Services.AddHttpClient();

        // Core services — same as Harbor.Cli.Hosting.HostBuilder.RegisterCore.
        builder.Services.AddSingleton<ITokenEstimator, HeuristicTokenEstimator>();
        builder.Services.AddSingleton<IEventBus>(sp => new InMemoryEventBus(
            sp.GetRequiredService<ILogger<InMemoryEventBus>>()));
        builder.Services.AddSingleton<ISystemPromptBuilder>(sp => new SystemPromptBuilder(
            sp.GetRequiredService<ILogger<SystemPromptBuilder>>()));
        builder.Services.AddSingleton<MessageConverter>();
        builder.Services.AddSingleton<IAgentLoop, AgentLoop>();
        builder.Services.AddSingleton<IAgent, DefaultAgent>();
        // Forward IAgentRunner → IAgent so DI resolution (and the Excubo
        // DependencyInjectionValidation analyzer) is satisfied. IAgent extends
        // IAgentRunner; this is the canonical "interface forwarded to a concrete
        // service that implements it" pattern documented in the MS DI docs.
        builder.Services.AddSingleton<IAgentRunner>(sp => sp.GetRequiredService<IAgent>());

        // ── Per-app Avalonia configuration (~/.harbor/avalonia.json) ──
        // Non-overlapping with CLI/WPF/MAUI/Blazor config files AND with the
        // shared ~/.harbor/config.json. JsonAppConfigStore handles atomic
        // write (temp + rename) + SemaphoreSlim thread safety.
        //
        // We load the config eagerly using a *bootstrap* logger factory so we
        // can register the loaded AvaloniaConfig as a singleton before the
        // host is built. The previous pattern called BuildServiceProvider()
        // twice just to get an ILogger — that creates a parallel DI container
        // that disposes out from under us and is flagged by the .NET analyser
        // as an anti-pattern.
        // NB: no `using` here — the bootstrap logger factory must outlive this
        // method because its loggers are passed to long-lived singletons
        // (ToolRegistry, ProviderRegistry, InMemoryMcpRegistry) constructed
        // eagerly below. Disposing the factory at end-of-method would silence
        // those singletons. The factory is intentionally leaked to process
        // lifetime (same effective lifetime as the previous tempSp pattern).
        // Bootstrap logger — uses Serilog directly (before DI is built).
        var serilogLogger = Harbor.Logging.LoggerSetup.Create(
            appPrefix: "avalonia",
            logDir: Path.Combine(homeDir, ".harbor", "logs"),
            consoleLevel: Serilog.Events.LogEventLevel.Warning,
            fileLevel: Serilog.Events.LogEventLevel.Debug);
        var bootstrapLoggerFactory = new SerilogLoggerFactory(serilogLogger, true);
        var bootstrapConfigLogger = bootstrapLoggerFactory.CreateLogger<JsonAppConfigStore<AvaloniaConfig>>();
        var configStore = new JsonAppConfigStore<AvaloniaConfig>(
            new AvaloniaConfig(),
            bootstrapConfigLogger);
        builder.Services.AddSingleton<IAppConfigStore<AvaloniaConfig>>(sp =>
            new JsonAppConfigStore<AvaloniaConfig>(
                new AvaloniaConfig(),
                sp.GetRequiredService<ILogger<JsonAppConfigStore<AvaloniaConfig>>>()));
        var avaloniaConfigResult = await configStore.LoadAsync().ConfigureAwait(false);
        var avaloniaConfig = avaloniaConfigResult.IsSuccess
            ? avaloniaConfigResult.Value
            : new AvaloniaConfig();
        builder.Services.AddSingleton(avaloniaConfig);

        // ── Shared common configuration (~/.harbor/config.json) ──
        // CommonConfig holds API keys, default provider/model, storage backend,
        // log level, permissions, plugins, network, compaction — every field
        // that is shared across ALL Harbor apps. Loaded eagerly so the
        // Avalonia composition root can read StorageBackend / LogLevel / etc.
        // synchronously. Same atomic-write + thread-safe pattern as
        // JsonAppConfigStore<T>. Reuses the bootstrap logger factory to avoid
        // the BuildServiceProvider() anti-pattern.
        var bootstrapCommonLogger = bootstrapLoggerFactory.CreateLogger<JsonCommonConfigStore>();
        var commonStore = new JsonCommonConfigStore(
            new CommonConfig(),
            bootstrapCommonLogger);
        var commonConfigResult = await commonStore.LoadAsync().ConfigureAwait(false);
        var commonConfig = commonConfigResult.IsSuccess
            ? commonConfigResult.Value
            : new CommonConfig();
        builder.Services.AddSingleton<ICommonConfigStore>(sp =>
            new JsonCommonConfigStore(
                new CommonConfig(),
                sp.GetRequiredService<ILogger<JsonCommonConfigStore>>()));
        builder.Services.AddSingleton(commonConfig);

        // ── Auth resolver + model catalog for OpenAI-compatible providers ──
        // The wizard persists API keys to CommonConfig.ApiKeys. This resolver
        // reads them (falling back to env vars like KILO_API_KEY) so providers
        // registered from providers/*.json can authenticate. The model catalog
        // fetches + caches the /models endpoint per provider.
        var authResolver = new CommonConfigAuthResolver(
            commonStore,
            bootstrapLoggerFactory.CreateLogger<CommonConfigAuthResolver>());
        var modelCatalog = new DynamicModelCatalog(
            new HttpClient(),
            Path.Combine(harborDir, "cache", "providers"),
            bootstrapLoggerFactory.CreateLogger<DynamicModelCatalog>());

        // ── Composite: CommonConfig + AvaloniaConfig ──
        builder.Services.AddSingleton<CompositeConfig<AvaloniaConfig>>(sp =>
            new CompositeConfig<AvaloniaConfig>(
                sp.GetRequiredService<CommonConfig>(),
                sp.GetRequiredService<AvaloniaConfig>()));

        // Storage — opt-in via HARBOR_STORAGE env var. The default comes from
        // CommonConfig.StorageBackend (shared across every Harbor app) and
        // falls back to "memory" (ephemeral) for the Avalonia desktop shell.
        string storage = Environment.GetEnvironmentVariable("HARBOR_STORAGE")
            ?? (string.IsNullOrEmpty(commonConfig.StorageBackend) ? "memory" : commonConfig.StorageBackend);
        builder.Services.AddSingleton<ISessionStore>(sp => storage.ToLowerInvariant() switch
        {
            "jsonl" => new JsonlSessionStore(sessionsDir, sp.GetRequiredService<ILogger<JsonlSessionStore>>()),
            _ => new MemorySessionStore()
        });

        // Build registries eagerly so the agent can be initialized with them.
        // Use the bootstrap logger factory (created above) instead of a throwaway
        // BuildServiceProvider() call — the parallel container anti-pattern was
        // flagged by DeepSeek review and is unsafe under disposables.
        var loggerFactory = bootstrapLoggerFactory;

        // Tools — a subset of the builtin tools (no MCP, no WebFetch to avoid HTTP
        // policy decisions; the user can add them via Settings later).
        var toolRegistry = new ToolRegistry();
        var tb = new ToolRegistryBuilder(toolRegistry);
        tb.AddTool(() => new ReadTool(loggerFactory.CreateLogger<ReadTool>()));
        tb.AddTool(() => new WriteTool(loggerFactory.CreateLogger<WriteTool>()));
        tb.AddTool(() => new EditTool(loggerFactory.CreateLogger<EditTool>()));
        tb.AddTool(() => new BashTool(loggerFactory.CreateLogger<BashTool>()));
        tb.AddTool(() => new GlobTool(loggerFactory.CreateLogger<GlobTool>()));
        tb.AddTool(() => new GrepTool(loggerFactory.CreateLogger<GrepTool>()));
        tb.AddTool(() => new LsTool(loggerFactory.CreateLogger<LsTool>()));
        tb.AddTool(() => new PatchTool(loggerFactory.CreateLogger<PatchTool>()));
        tb.AddTool(() => new NotebookTool(loggerFactory.CreateLogger<NotebookTool>()));
        tb.AddTool(() => new TreeTool(loggerFactory.CreateLogger<TreeTool>()));
        toolRegistry.Freeze();

        // Providers — Ollama (native client, works offline) + all OpenAI-
        // compatible providers discovered from providers/*.json (Kilocode,
        // OpenRouter, DeepSeek, Groq, Mistral, xAI, Together, Fireworks,
        // Cerebras, OpenAI, vLLM). This mirrors the CLI's
        // ProviderRegistration.RegisterJsonProviders but inlines the logic
        // (Avalonia doesn't reference Harbor.Cli.Hosting).
        var providerRegistry = new ProviderRegistry(loggerFactory.CreateLogger<ProviderRegistry>());
        var pb = new ProviderRegistryBuilder(providerRegistry);
        pb.AddProvider("ollama", () => new OllamaLlmClient(
            new HttpClient
            {
                BaseAddress = new Uri(Environment.GetEnvironmentVariable("OLLAMA_HOST") ?? "http://localhost:11434"),
                // 10s timeout — the default 100s makes the UI feel frozen when
                // Ollama isn't running. The ProviderBrowserViewModel adds its
                // own 5s cancellation token on top, so a missing Ollama is
                // surfaced as a quick "no models" rather than a 100s hang.
                Timeout = TimeSpan.FromSeconds(10),
            },
            new OllamaConfig(),
            loggerFactory.CreateLogger<OllamaLlmClient>()));
        RegisterJsonProviders(pb, authResolver, modelCatalog, loggerFactory);
        providerRegistry.Freeze();

        // Agent registry — register default code/plan/explore agents using the
        // CommonConfig default provider/model (or HARBOR_MODEL env override).
        // The wizard saves DefaultProvider="kilocode" + DefaultModel="tencent/hy3:free"
        // — combine them into "kilocode/tencent/hy3:free" and split on the first
        // slash so the agent knows which provider + model to call.
        var agentRegistry = new AgentRegistry();
        var ab = new AgentRegistryBuilder(agentRegistry);
        string defaultModel = ResolveDefaultModel(commonConfig);
        string[] parts = defaultModel.Split('/', 2);
        string defaultProviderId = parts[0];
        string defaultModelId = parts.Length > 1 ? parts[1] : defaultModel;
        ab.AddAgent(AgentDefinition.CodeDefault(defaultModelId, defaultProviderId));
        ab.AddAgent(AgentDefinition.PlanDefault(defaultModelId, defaultProviderId));
        ab.AddAgent(AgentDefinition.ExploreDefault(defaultModelId, defaultProviderId));

        // Compaction + permissions.
        builder.Services.AddSingleton<ICompactionService>(sp => new CompactionService(
            sp.GetRequiredService<ITokenEstimator>(),
            providerRegistry,
            sp.GetRequiredService<ILogger<CompactionService>>()));
        builder.Services.AddSingleton<IPermissionService>(sp => new PermissionService(
            agentRegistry,
            sp.GetRequiredService<ILogger<PermissionService>>()));

        // Register the eagerly-constructed registries as singletons.
        builder.Services.AddSingleton<IToolRegistry>(toolRegistry);
        builder.Services.AddSingleton<IProviderRegistry>(providerRegistry);
        builder.Services.AddSingleton<IAgentRegistry>(agentRegistry);
        builder.Services.AddSingleton<IMcpRegistry>(new InMemoryMcpRegistry(
            loggerFactory.CreateLogger<InMemoryMcpRegistry>()));

        // Harbor TUI TEA store + effect host — the single source of truth for the chat UI.
        builder.Services.AddSingleton<UiStore>();
        builder.Services.AddSingleton<TuiEffectHost>(sp =>
        {
            var agent = sp.GetRequiredService<IAgentRunner>();
            var store = sp.GetRequiredService<UiStore>();
            var logger = sp.GetRequiredService<ILogger<TuiEffectHost>>();
            return new TuiEffectHost(agent, store, slash: null, appCt: default, logger);
        });

        // App-local services.
        builder.Services.AddSingleton<ThemeService>();
        builder.Services.AddSingleton<DialogService>();
        builder.Services.AddSingleton<AvaloniaFilePicker>();
        builder.Services.AddSingleton<SessionManager>();
        builder.Services.AddSingleton<GitService>();
        builder.Services.AddSingleton<ToastService>();
        // AvaloniaDispatcherAdapter is the UiStore→UI-thread bridge. Bound to
        // the UiStore exactly once below (after host.Build()) so VMs that
        // resolve the adapter can subscribe to OnUiThread without racing with
        // a Bind call from another VM's constructor.
        builder.Services.AddSingleton<AvaloniaDispatcherAdapter>();

        // ── IHarborClient (proof of concept) ──────────────────────────────
        // Register IHarborClient based on HARBOR_MODE env var:
        //   inprocess (default) → InProcessHarborClient calls IAgent/etc directly.
        //   ipc-client           → IpcHarborClient talks to a separately-running
        //                         Harbor.App.Cli process with HARBOR_MODE=ipc-server.
        // The Avalonia app does NOT support ipc-server mode itself — the desktop
        // app shouldn't be a background server. Use the CLI for that.
        string ipcMode = Environment.GetEnvironmentVariable("HARBOR_MODE") ?? "inprocess";
        string ipcPipe = Environment.GetEnvironmentVariable("HARBOR_IPC_PIPE") ?? "harbor-ipc";
        switch (ipcMode.ToLowerInvariant())
        {
            case "ipc-client":
                builder.Services.UseIpcHarborClient(ipcPipe);
                break;
            default:
                builder.Services.UseInProcessHarborClient();
                break;
        }

        // ViewModels — long-lived shell VMs are Singletons so that resolves are
        // stable across the app lifetime. Transient resolution of MainViewModel
        // was a DeepSeek-flagged bug: CommandPaletteViewModel resolves MainViewModel
        // on every command invocation, and a transient MainViewModel meant each
        // invocation got a fresh shell with no bound ChatViewModel/Sessions/etc.
        //
        // The shell VMs (Main/Chat/SessionList/CommandPalette) form a singleton
        // cluster — they reference each other and share the UiStore subscription.
        //
        // Edit-style VMs (CodeEditor/Diff/TokenUsage) stay Transient because they
        // hold per-document state that the user may want to discard on close.
        builder.Services.AddSingleton<MainViewModel>();
        builder.Services.AddSingleton<ChatViewModel>();
        builder.Services.AddSingleton<SessionListViewModel>();
        builder.Services.AddSingleton<CommandPaletteViewModel>();
        builder.Services.AddTransient<ProviderBrowserViewModel>();
        builder.Services.AddTransient<SettingsViewModel>();
        builder.Services.AddTransient<CodeEditorViewModel>();
        builder.Services.AddTransient<DiffViewModel>();
        builder.Services.AddTransient<TokenUsageViewModel>();
        // Onboarding VM is transient — created fresh each time the wizard runs
        // (first launch, or re-run from Settings). Holds per-run state (current
        // step, typed API key) that we explicitly want to discard on close.
        builder.Services.AddTransient<OnboardingViewModel>();

        // ── Experimental Orca shell VM (Task F2) ────────────────────────────
        // Singleton so it survives across the app lifetime (same lifetime
        // as the MainViewModel it wraps). Resolved ONLY when HARBOR_SHELL=orca
        // (App.ShowMain branches on App.IsOrcaShell); in classic mode the
        // singleton is constructed lazily on first resolve and never resolved,
        // so its constructor side-effects (event subscriptions on MainViewModel)
        // never run.
        builder.Services.AddSingleton<OrcaShellViewModel>();

        var host = builder.Build();

        // Bind the UiStore → AvaloniaDispatcherAdapter exactly once, here in the
        // composition root. ViewModels only subscribe to the dispatcher's
        // OnUiThread event (the Bind call is no longer made from VM constructors,
        // which previously caused duplicate subscriptions because MainViewModel and
        // ChatViewModel both called Bind during their construction).
        var uiStore = host.Services.GetRequiredService<UiStore>();
        var dispatcherAdapter = host.Services.GetRequiredService<AvaloniaDispatcherAdapter>();
        dispatcherAdapter.Bind(uiStore);

        // Initialize the agent with a fresh session so the user can start chatting
        // immediately. The SessionManager owns the active session and re-initializes
        // the agent whenever the user switches/branches. NOTE: when onboarding is
        // required, the wizard runs AFTER BuildAsync returns — App.axaml.cs calls
        // SessionManager.EnsureDefaultSessionAsync() again once the wizard saves
        // the new CommonConfig so the agent picks up the user's chosen provider.
        var sessionManager = host.Services.GetRequiredService<SessionManager>();
        await sessionManager.EnsureDefaultSessionAsync().ConfigureAwait(false);

        return host;
    }

    /// <summary>
    ///     Resolve the default "provider/model" string from (1) the HARBOR_MODEL
    ///     env var, or (2) CommonConfig's DefaultProvider + DefaultModel. The
    ///     model may already contain the provider prefix (e.g. the user typed
    ///     "kilocode/tencent/hy3:free" in the wizard) — in that case we use it
    ///     as-is. Otherwise we prepend the provider: "kilocode" + "/" +
    ///     "tencent/hy3:free" → "kilocode/tencent/hy3:free".
    /// </summary>
    private static string ResolveDefaultModel(CommonConfig commonConfig)
    {
        string? env = Environment.GetEnvironmentVariable("HARBOR_MODEL");
        if (!string.IsNullOrWhiteSpace(env)) return env;

        string model = commonConfig.DefaultModel;
        string provider = commonConfig.DefaultProvider;
        string prefix = provider + "/";
        return model.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? model
            : prefix + model;
    }

    /// <summary>
    ///     Discover and register OpenAI-compatible providers from every
    ///     <c>providers/*.json</c> candidate directory. Mirrors the CLI's
    ///     <c>ProviderRegistration.RegisterJsonProviders</c> but inlined so
    ///     Avalonia doesn't need to reference <c>Harbor.Cli.Hosting</c>.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>Directories searched (first wins on id collision):</b>
    ///     </para>
    ///     <list type="number">
    ///         <item><c>~/.harbor/providers/</c> — user overrides.</item>
    ///         <item><c>&lt;exeDir&gt;/providers/</c> — bundled (single-file publish).</item>
    ///         <item>Walk up from <c>exeDir</c> looking for a sibling <c>providers/</c> — dev-clone layout.</item>
    ///     </list>
    ///     <para>
    ///         Providers with <c>apiType != "openai-compatible"</c> (e.g.
    ///         Anthropic, which needs its native client) and <c>ollama</c>
    ///         (already registered above with the native OllamaLlmClient) are
    ///         skipped. The first matching id wins; duplicates are silently
    ///         dropped so user overrides in <c>~/.harbor/providers/</c> take
    ///         precedence over bundled JSON.
    ///     </para>
    /// </remarks>
    private static void RegisterJsonProviders(
        ProviderRegistryBuilder pb,
        IAuthResolver authResolver,
        IModelCatalog modelCatalog,
        ILoggerFactory loggerFactory)
    {
        var seenIds = new HashSet<string>(StringComparer.Ordinal) { "ollama" };
        var logger = loggerFactory.CreateLogger(typeof(AppHost).FullName ?? "AppHost");

        foreach (string dir in FindProvidersDirectories())
        {
            if (!Directory.Exists(dir)) continue;
            foreach (string file in Directory.EnumerateFiles(dir, "*.json"))
            {
                try
                {
                    var result = ProviderConfig.LoadFromFile(file);
                    if (result.IsFailure)
                    {
                        logger.LogWarning("Skipping provider config '{File}': {Error}", file, result.Error);
                        continue;
                    }
                    var config = result.Value;
                    if (config.ApiType != "openai-compatible")
                    {
                        logger.LogDebug("Skipping provider '{Id}' (apiType={Type}, not openai-compatible)",
                            config.Id, config.ApiType);
                        continue;
                    }
                    if (!seenIds.Add(config.Id))
                    {
                        logger.LogDebug("Skipping duplicate provider '{Id}' from '{File}'", config.Id, file);
                        continue;
                    }

                    var http = new HttpClient { Timeout = TimeSpan.FromSeconds(config.Timeout) };
                    config.Quirks = ProviderCompatFlags.For(config.GetProviderId());
                    var configRef = config;
                    pb.AddProvider(config.Id, () => new OpenAiCompatibleLlmClient(
                        http, configRef, authResolver, modelCatalog,
                        loggerFactory.CreateLogger<OpenAiCompatibleLlmClient>()));
                    logger.LogInformation("Registered OpenAI-compatible provider '{Id}' from '{File}'", config.Id, file);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Failed to load provider config '{File}'", file);
                }
            }
        }
    }

    /// <summary>
    ///     Enumerate every candidate providers directory in precedence order:
    ///     user config (<c>~/.harbor/providers/</c>) first, then bundled
    ///     (<c>&lt;exeDir&gt;/providers/</c> and any ancestor). User config wins
    ///     on id collisions so E2E tests (and power users) can override a
    ///     bundled provider with their own JSON.
    /// </summary>
    private static IEnumerable<string> FindProvidersDirectories()
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        yield return Path.Combine(home, ".harbor", "providers");

        string exeDir = AppContext.BaseDirectory;
        yield return Path.Combine(exeDir, "providers");

        string? current = exeDir;
        for (int i = 0; i < 8 && current is not null; i++)
        {
            yield return Path.Combine(current, "providers");
            current = Path.GetDirectoryName(current);
        }
    }

    private static void ConfigureLogging(HostApplicationBuilder builder)
    {
        string homeDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string logDir = Path.Combine(homeDir, ".harbor", "logs");
        
        // Use Serilog for all logging — writes to BOTH file and console.
        // File: ~/.harbor/logs/harbor-avalonia-{timestamp}.log (always Debug level)
        // Console: stderr (Avalonia doesn't use stdout for UI)
        var serilogLogger = Harbor.Logging.LoggerSetup.Create(
            appPrefix: "avalonia",
            logDir: logDir,
            consoleLevel: Serilog.Events.LogEventLevel.Warning,  // console only shows warnings+ in UI mode
            fileLevel: Serilog.Events.LogEventLevel.Debug);       // file captures everything
        
        // Clean up old logs
        Harbor.Logging.LoggerSetup.CleanupOldLogs(logDir, 50);
        
        // Replace .NET logging with Serilog
        builder.Logging.ClearProviders();
        builder.Services.AddSerilog(serilogLogger, dispose: true);
        builder.Logging.SetMinimumLevel(LogLevel.Debug);
    }
}
