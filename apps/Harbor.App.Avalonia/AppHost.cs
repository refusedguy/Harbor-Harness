using Harbor.App.Avalonia.Configuration;
using Harbor.App.Avalonia.Services;
using Harbor.App.Avalonia.ViewModels;
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
using Harbor.Storage.Jsonl;
using Harbor.Storage.Memory;
using Harbor.Tools.Builtin;
using Harbor.Ui.Framework.State;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
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
    [Exposes(typeof(ToastService))]
    [Exposes(typeof(AvaloniaDispatcherAdapter))]
    [Exposes(typeof(IHarborClient))]
    [Exposes(typeof(IAppConfigStore<AvaloniaConfig>))]
    [Exposes(typeof(AvaloniaConfig))]
    [Exposes(typeof(ICommonConfigStore))]
    [Exposes(typeof(CommonConfig))]
    [Exposes(typeof(CompositeConfig<AvaloniaConfig>))]
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
        var bootstrapLoggerFactory = LoggerFactory.Create(b => b.AddSimpleConsole());
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

        // Providers — start with just Ollama (works offline if user has a local server).
        // OpenAI-compatible providers are added in SettingsView when the user configures them.
        var providerRegistry = new ProviderRegistry(loggerFactory.CreateLogger<ProviderRegistry>());
        var pb = new ProviderRegistryBuilder(providerRegistry);
        pb.AddProvider("ollama", () => new OllamaLlmClient(
            new HttpClient { BaseAddress = new Uri(Environment.GetEnvironmentVariable("OLLAMA_HOST") ?? "http://localhost:11434") },
            new OllamaConfig(),
            loggerFactory.CreateLogger<OllamaLlmClient>()));
        providerRegistry.Freeze();

        // Agent registry — register default code/plan/explore agents.
        var agentRegistry = new AgentRegistry();
        var ab = new AgentRegistryBuilder(agentRegistry);
        string defaultModel = Environment.GetEnvironmentVariable("HARBOR_MODEL") ?? "ollama/qwen2.5-coder:7b";
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
        // the agent whenever the user switches/branches.
        var sessionManager = host.Services.GetRequiredService<SessionManager>();
        await sessionManager.EnsureDefaultSessionAsync().ConfigureAwait(false);

        return host;
    }

    private static void ConfigureLogging(HostApplicationBuilder builder)
    {
        string? env = Environment.GetEnvironmentVariable("HARBOR_LOGLEVEL");
        LogLevel level = env is not null && Enum.TryParse(env, true, out LogLevel parsed)
            ? parsed
            : LogLevel.Warning;
        builder.Logging.ClearProviders();
        builder.Logging.AddSimpleConsole(o =>
        {
            o.SingleLine = true;
            o.TimestampFormat = "HH:mm:ss ";
        });
        builder.Logging.SetMinimumLevel(level);
    }
}
