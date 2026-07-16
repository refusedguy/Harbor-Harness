using Harbor.Tui.Abstractions;
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
using Harbor.Cli.Logging;
using Harbor.Core.Agents;
using Harbor.Core.Configuration;
using Harbor.Core.Onboarding;
using Harbor.Core.Permissions;
using Harbor.Core.Sessions;
using Harbor.Providers.Anthropic;
using Harbor.Providers.Ollama;
using Harbor.Providers.OpenAI;
using Harbor.Providers.OpenAiCompatible;
using Harbor.Storage.Jsonl;
using Harbor.Storage.Memory;
using Harbor.Storage.Sqlite;
using Harbor.Tools.Builtin;
using Harbor.Tui.Ansi;
using Harbor.Tui.Plain;
using Harbor.Tui.Spectre;
using Harbor.Tui.Spectre.Fullscreen;
using System.Reflection;
using System.Text.Json;
using System.Threading.Channels;

namespace Harbor.Cli.Hosting;

/// <summary>
/// DI host configuration — single responsibility: wire services.
/// Extracted from Program.cs to reduce god object.
/// </summary>
internal static class HostBuilder
{
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
        builder.Logging.AddProvider(new FileLoggerProvider(logLevel));
        if (logLevel <= LogLevel.Information)
        {
            builder.Logging.AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "HH:mm:ss "; });
        }
        builder.Logging.SetMinimumLevel(logLevel);
    }

    private static void RegisterCore(HostApplicationBuilder builder)
    {
        builder.Services.AddSingleton<IConfigStore>(sp => new JsonConfigStore(
            logger: sp.GetRequiredService<ILogger<JsonConfigStore>>()));
        builder.Services.AddSingleton<AuthStore>();
        builder.Services.AddSingleton<OnboardingWizard>();
        builder.Services.AddSingleton<ITokenEstimator, HeuristicTokenEstimator>();
        builder.Services.AddSingleton<IEventBus>(sp => new InMemoryEventBus(maxScrollback: 1000));
        builder.Services.AddSingleton<ISystemPromptBuilder, SystemPromptBuilder>();
        builder.Services.AddSingleton<MessageConverter>();
        builder.Services.AddSingleton<IAgentLoop, AgentLoop>();
        builder.Services.AddSingleton<IAgent, DefaultAgent>();
    }

    private static void RegisterRegistries(HostApplicationBuilder builder, string harborDir)
    {
        var configStore = new JsonConfigStore(logger: builder.Services.BuildServiceProvider().GetRequiredService<ILogger<JsonConfigStore>>());
        var config = configStore.LoadAsync().GetAwaiter().GetResult().Value;
        ApplyEnvOverrides(config);

        builder.Services.AddSingleton<IAgentRegistry>(sp => CreateAgentRegistry(config));
        builder.Services.AddSingleton<IToolRegistry>(sp => CreateToolRegistry(sp));
        builder.Services.AddSingleton<IProviderRegistry>(sp => CreateProviderRegistry(sp, harborDir, config));
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

    private static ToolRegistry CreateToolRegistry(IServiceProvider sp)
    {
        var registry = new ToolRegistry();
        var tb = new ToolRegistryBuilder(registry);
        tb.AddTool<ReadTool>();
        tb.AddTool<WriteTool>();
        tb.AddTool<EditTool>();
        tb.AddTool<BashTool>();
        tb.AddTool<GlobTool>();
        tb.AddTool<GrepTool>();
        tb.AddTool<LsTool>();
        tb.AddTool(new TaskTool(sp.GetRequiredService<IAgentRegistry>()));
        registry.Freeze();
        return registry;
    }

    private static ProviderRegistry CreateProviderRegistry(IServiceProvider sp, string harborDir, HarborConfig config)
    {
        var registry = new ProviderRegistry();
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
        return registry;
    }

    private static void RegisterStorage(HostApplicationBuilder builder, string sessionsDir, string sqlitePath)
    {
        string storage = Environment.GetEnvironmentVariable("HARBOR_STORAGE") ?? "jsonl";
        builder.Services.AddSingleton<ISessionStore>(sp => storage.ToLowerInvariant() switch
        {
            "memory" => new MemorySessionStore(),
            "sqlite" => new SqliteSessionStore(sqlitePath, sp.GetRequiredService<ILogger<SqliteSessionStore>>()),
            _ => new JsonlSessionStore(sessionsDir, sp.GetRequiredService<ILogger<JsonlSessionStore>>())
        });
    }

    private static void RegisterTui(HostApplicationBuilder builder)
    {
        string tui = Environment.GetEnvironmentVariable("HARBOR_TUI") ?? "ansi";
        builder.Services.AddSingleton<ITuiRenderer>(sp => tui.ToLowerInvariant() switch
        {
            "plain" => new PlainTuiRenderer(),
            "spectre" => new SpectreTuiRenderer(sp.GetRequiredService<ILogger<SpectreTuiRenderer>>()),
            "fullscreen" => new FullscreenTuiRenderer(sp.GetRequiredService<ILogger<FullscreenTuiRenderer>>()),
            "spectre-tui" => new Harbor.Tui.SpectreTui.SpectreTuiRenderer(sp.GetRequiredService<ILogger<Harbor.Tui.SpectreTui.SpectreTuiRenderer>>()),
            "terminal-gui" => new Harbor.Tui.TerminalGui.TerminalGuiRenderer(sp.GetRequiredService<ILogger<Harbor.Tui.TerminalGui.TerminalGuiRenderer>>()),
            "termina" => new Harbor.Tui.Termina.TerminaRenderer(sp.GetRequiredService<ILogger<Harbor.Tui.Termina.TerminaRenderer>>()),
            "razor" => new Harbor.Tui.RazorConsole.RazorConsoleRenderer(sp.GetRequiredService<ILogger<Harbor.Tui.RazorConsole.RazorConsoleRenderer>>()),
            _ => new AnsiTuiRenderer(sp.GetRequiredService<ILogger<AnsiTuiRenderer>>())
        });
    }

    private static void RegisterHttpClients(HostApplicationBuilder builder)
    {
        builder.Services.AddHttpClient("anthropic");
        builder.Services.AddHttpClient("openai");
        builder.Services.AddHttpClient("ollama");
        builder.Services.AddHttpClient("providers");
        builder.Services.AddHttpClient("default");
    }
}
