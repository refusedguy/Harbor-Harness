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
    [Exposes(typeof(IAppConfigStore<AvaloniaConfig>))]
    [Exposes(typeof(AvaloniaConfig))]
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

        // ── Per-app Avalonia configuration (~/.harbor/avalonia.json) ──
        // Non-overlapping with CLI/WPF/MAUI/Blazor config files. JsonAppConfigStore
        // handles atomic write (temp + rename) + SemaphoreSlim thread safety.
        builder.Services.AddSingleton<IAppConfigStore<AvaloniaConfig>>(sp =>
            new JsonAppConfigStore<AvaloniaConfig>(
                new AvaloniaConfig(),
                sp.GetRequiredService<ILogger<JsonAppConfigStore<AvaloniaConfig>>>()));
        // Eagerly load AvaloniaConfig so the rest of the composition root
        // (ThemeService, MainViewModel) can resolve it synchronously.
        var configStore = new JsonAppConfigStore<AvaloniaConfig>(
            new AvaloniaConfig(),
            builder.Services.BuildServiceProvider()
                .GetRequiredService<ILogger<JsonAppConfigStore<AvaloniaConfig>>>());
        var avaloniaConfigResult = await configStore.LoadAsync().ConfigureAwait(false);
        var avaloniaConfig = avaloniaConfigResult.IsSuccess
            ? avaloniaConfigResult.Value
            : new AvaloniaConfig();
        builder.Services.AddSingleton(avaloniaConfig);

        // Storage — opt-in via HARBOR_STORAGE env var. Defaults to in-memory (ephemeral).
        string storage = Environment.GetEnvironmentVariable("HARBOR_STORAGE") ?? "memory";
        builder.Services.AddSingleton<ISessionStore>(sp => storage.ToLowerInvariant() switch
        {
            "jsonl" => new JsonlSessionStore(sessionsDir, sp.GetRequiredService<ILogger<JsonlSessionStore>>()),
            _ => new MemorySessionStore()
        });

        // Build registries eagerly so the agent can be initialized with them.
        var tempSp = builder.Services.BuildServiceProvider();
        var loggerFactory = tempSp.GetRequiredService<ILoggerFactory>();

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

        // ViewModels — registered as transient because some hold session-scoped state.
        builder.Services.AddTransient<MainViewModel>();
        builder.Services.AddTransient<ChatViewModel>();
        builder.Services.AddTransient<SessionListViewModel>();
        builder.Services.AddTransient<ProviderBrowserViewModel>();
        builder.Services.AddTransient<SettingsViewModel>();
        builder.Services.AddTransient<CodeEditorViewModel>();
        builder.Services.AddTransient<DiffViewModel>();
        builder.Services.AddTransient<TokenUsageViewModel>();
        builder.Services.AddTransient<CommandPaletteViewModel>();

        var host = builder.Build();

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
