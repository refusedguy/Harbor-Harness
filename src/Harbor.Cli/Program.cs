using System.Reflection;
using System.Text.Json;
using System.Threading.Channels;
using CSharpFunctionalExtensions;
using Harbor.Abstractions.Agents;
using Harbor.Abstractions.Events;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Models.Identifiers;
using Harbor.Abstractions.Permissions;
using Harbor.Abstractions.Providers;
using Harbor.Abstractions.Sessions;
using Harbor.Abstractions.Tools;
using Harbor.Abstractions.Tui;
using Harbor.Cli.Commands;
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
using Harbor.Tui.Abstractions;
using Harbor.Tui.Ansi;
using Harbor.Tui.Plain;
using Harbor.Tui.Spectre;
using Harbor.Tui.Spectre.Fullscreen;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Harbor.Cli.Logging;
namespace Harbor.Cli;
public static class Program
{
    /// <summary>
    ///     Resolve the minimum log level from the <c>--loglevel &lt;level&gt;</c> CLI flag or the
    ///     <c>HARBOR_LOGLEVEL</c> environment variable. Defaults to <see cref="LogLevel.Warning" />.
    /// </summary>
    /// <summary>
    ///     Strip <c>--loglevel/-ll</c> and its value from the argument list so they don't leak
    ///     into the user prompt (e.g. for the <c>ask</c> command).
    /// </summary>
    private static string[] StripLogArgs(string[] args)
    {
        var result = new List<string>(args.Length);
        var i = 0;
        while (i < args.Length)
        {
            if (args[i].Equals("--loglevel", StringComparison.OrdinalIgnoreCase) ||
                args[i].Equals("-ll", StringComparison.OrdinalIgnoreCase))
            {
                i += 2; // skip the flag and its following value
                continue;
            }

            result.Add(args[i]);
            i++;
        }
        return result.ToArray();
    }

    private static LogLevel ResolveLogLevel(string[] args)
    {
        string? raw = null;

        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i].Equals("--loglevel", StringComparison.OrdinalIgnoreCase) ||
                args[i].Equals("-ll", StringComparison.OrdinalIgnoreCase))
            {
                raw = args[i + 1];
                break;
            }
        }

        raw ??= Environment.GetEnvironmentVariable("HARBOR_LOGLEVEL");

        if (string.IsNullOrWhiteSpace(raw))
            return LogLevel.Warning;

        return Enum.TryParse<LogLevel>(raw, true, out var level) ? level : LogLevel.Warning;
    }

    public static async Task<int> Main(string[] args)
    {
        if (args.Length == 0)
        {
            return await RunInteractiveAsync(args);
        }

        string command = args[0].ToLowerInvariant();
        return command switch
        {
            "ask" => await RunAskAsync(args.Skip(1).ToArray()),
            "providers" => await RunListProvidersAsync(),
            "models" => await RunListModelsAsync(args.Skip(1).FirstOrDefault()),
            "sessions" => await RunListSessionsAsync(),
            "tui" => PrintTuiOptions(),
            "storage" => PrintStorageOptions(),
            "setup" => await RunSetupAsync(),
            "auth" => await RunAuthAsync(args.Skip(1).ToArray()),
            "config" => await RunConfigAsync(args.Skip(1).ToArray()),
            "help" or "--help" or "-h" => PrintHelp(),
            "version" or "--version" or "-v" => PrintVersion(),
            _ => await RunInteractiveAsync()
        };
    }

    private static IHost BuildHost(params string[] args)
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

        builder.Logging.ClearProviders();

        // Default to Warning to keep the TUI output clean. Override via the
        // HARBOR_LOGLEVEL env var or the --loglevel <level> CLI flag (e.g.
        // "debug", "information", "trace") for debugging the agent pipeline.
        var logLevel = ResolveLogLevel(args);

        // File logger — always on (dated file in <exe>/logs) so the full
        // startup→completion pipeline is captured regardless of TUI noise.
        var fileLogger = new FileLoggerProvider(logLevel);
        builder.Logging.AddProvider(fileLogger);

        if (logLevel <= LogLevel.Information)
        {
            builder.Logging.AddSimpleConsole(o =>
            {
                o.SingleLine = true;
                o.TimestampFormat = "HH:mm:ss ";
            });
        }
        builder.Logging.SetMinimumLevel(logLevel);

        // Configuration
        builder.Services.AddSingleton<IConfigStore>(sp => new JsonConfigStore(
            logger: sp.GetRequiredService<ILogger<JsonConfigStore>>()));
        builder.Services.AddSingleton<AuthStore>();
        builder.Services.AddSingleton<OnboardingWizard>();

        // Core services
        builder.Services.AddSingleton<ITokenEstimator, HeuristicTokenEstimator>();
        builder.Services.AddSingleton<IEventBus>(sp => new InMemoryEventBus(maxScrollback: 1000));
        builder.Services.AddSingleton<ISystemPromptBuilder, SystemPromptBuilder>();
        builder.Services.AddSingleton<MessageConverter>();
        builder.Services.AddSingleton<IAgentLoop, AgentLoop>();
        builder.Services.AddSingleton<IAgent, DefaultAgent>();

        // Load config synchronously for registries
        var configStore = new JsonConfigStore(logger: builder.Services.BuildServiceProvider().GetRequiredService<ILogger<JsonConfigStore>>());
        var config = configStore.LoadAsync().GetAwaiter().GetResult().Value;

        // Override with HARBOR_MODEL env var if set
        string? envModel = Environment.GetEnvironmentVariable("HARBOR_MODEL");
        if (!string.IsNullOrEmpty(envModel))
        {
            config.Model = envModel;
            string[] parts = envModel.Split('/', 2);
            config.Provider = parts[0];
        }

        // Registries
        builder.Services.AddSingleton<IAgentRegistry>(sp =>
        {
            var registry = new AgentRegistry();
            var builder = new AgentRegistryBuilder(registry);

            string defaultModel = config.Model;
            string[] parts = defaultModel.Split('/', 2);
            string providerId = parts[0];
            string modelId = parts.Length > 1 ? parts[1] : defaultModel;

            builder.AddAgent(AgentDefinition.CodeDefault(modelId, providerId));
            builder.AddAgent(AgentDefinition.PlanDefault(modelId, providerId));
            builder.AddAgent(AgentDefinition.ExploreDefault(modelId, providerId));

            return registry;
        });

        builder.Services.AddSingleton<IToolRegistry>(sp =>
        {
            var registry = new ToolRegistry();
            var builder = new ToolRegistryBuilder(registry);

            builder.AddTool<ReadTool>();
            builder.AddTool<WriteTool>();
            builder.AddTool<EditTool>();
            builder.AddTool<BashTool>();
            builder.AddTool<GlobTool>();
            builder.AddTool<GrepTool>();
            builder.AddTool<LsTool>();
            builder.AddTool(new TaskTool(sp.GetRequiredService<IAgentRegistry>()));

            registry.Freeze();
            return registry;
        });

        builder.Services.AddSingleton<IProviderRegistry>(sp =>
        {
            var registry = new ProviderRegistry();
            var providerBuilder = new ProviderRegistryBuilder(registry);
            var httpClientFactory = sp.GetRequiredService<IHttpClientFactory>();
            var loggerFactory = sp.GetRequiredService<ILoggerFactory>();
            string cacheDirResolved = Path.Combine(harborDir, "cache", "providers");
            var authStore = sp.GetRequiredService<AuthStore>();

            // 1. Native Anthropic provider
            providerBuilder.AddProvider("anthropic", () =>
            {
                var key = authStore.GetApiKeyAsync("anthropic").GetAwaiter().GetResult();
                return new AnthropicLlmClient(
                    httpClientFactory.CreateClient("anthropic"),
                    new AnthropicConfig(),
                    new ConfigAuthResolver(authStore, "anthropic"),
                    loggerFactory.CreateLogger<AnthropicLlmClient>());
            });

            // 2. Native OpenAI provider
            providerBuilder.AddProvider("openai", () => new OpenAILlmClient(
                httpClientFactory.CreateClient("openai"),
                new OpenAIConfig(),
                new ConfigAuthResolver(authStore, "openai"),
                loggerFactory.CreateLogger<OpenAILlmClient>()));

            // 3. Native Ollama provider (local, no auth)
            providerBuilder.AddProvider("ollama", () => new OllamaLlmClient(
                httpClientFactory.CreateClient("ollama"),
                new OllamaConfig(),
                loggerFactory.CreateLogger<OllamaLlmClient>()));

            // 4. Generic OpenAI-compatible providers from JSON configs and embedded resources
            RegisterJsonProviders(providerBuilder, httpClientFactory, loggerFactory, cacheDirResolved, authStore);

            registry.Freeze();
            return registry;
        });

        builder.Services.AddSingleton<ICompactionService>(sp =>
            new CompactionService(
                sp.GetRequiredService<ITokenEstimator>(),
                sp.GetRequiredService<IProviderRegistry>(),
                sp.GetRequiredService<ILogger<CompactionService>>()));

        builder.Services.AddSingleton<IPermissionService>(sp =>
            new PermissionService(
                sp.GetRequiredService<IAgentRegistry>(),
                sp.GetRequiredService<ILogger<PermissionService>>()));

        // Storage — choose via config or HARBOR_STORAGE env var
        string storage = config.Storage ?? Environment.GetEnvironmentVariable("HARBOR_STORAGE") ?? "jsonl";
        builder.Services.AddSingleton<ISessionStore>(sp => storage.ToLowerInvariant() switch
        {
            "memory" => new MemorySessionStore(),
            "sqlite" => new SqliteSessionStore(
                sqlitePath,
                sp.GetRequiredService<ILogger<SqliteSessionStore>>()),
            _ => new JsonlSessionStore(sessionsDir, sp.GetRequiredService<ILogger<JsonlSessionStore>>())
        });

        // TUI — choose via config or HARBOR_TUI env var
        string tui = config.Tui ?? Environment.GetEnvironmentVariable("HARBOR_TUI") ?? "ansi";
        builder.Services.AddSingleton<ITuiRenderer>(sp => tui.ToLowerInvariant() switch
        {
            "plain" => new PlainTuiRenderer(),
            "spectre" => new SpectreTuiRenderer(
                sp.GetRequiredService<ILogger<SpectreTuiRenderer>>()),
            "fullscreen" => new FullscreenTuiRenderer(
                sp.GetRequiredService<ILogger<FullscreenTuiRenderer>>()),
            _ => new AnsiTuiRenderer(sp.GetRequiredService<ILogger<AnsiTuiRenderer>>())
        });

        // HttpClient
        builder.Services.AddHttpClient("anthropic");
        builder.Services.AddHttpClient("openai");
        builder.Services.AddHttpClient("ollama");
        builder.Services.AddHttpClient("providers");
        builder.Services.AddHttpClient("default");

        return builder.Build();
    }

    private static void RegisterJsonProviders(
        IProviderRegistryBuilder builder,
        IHttpClientFactory httpClientFactory,
        ILoggerFactory loggerFactory,
        string cacheDir,
        AuthStore authStore)
    {
        var modelCatalog = new DynamicModelCatalog(
            httpClientFactory.CreateClient("providers"),
            cacheDir,
            loggerFactory.CreateLogger<DynamicModelCatalog>());

        // Load from filesystem
        string? providersDir = FindProvidersDirectory();
        if (providersDir is not null && Directory.Exists(providersDir))
        {
            var files = Directory.EnumerateFiles(providersDir, "*.json").ToList();
            foreach (string file in files)
            {
                RegisterJsonProvider(file, builder, httpClientFactory, authStore, modelCatalog, loggerFactory);
            }
        }

        // Load embedded provider configs
        foreach ((string name, string content) in LoadEmbeddedProviders())
        {
            try
            {
                var config = JsonSerializer.Deserialize<ProviderConfig>(content);
                if (config is null || string.IsNullOrEmpty(config.Id)) continue;
                if (config.Id is "anthropic" or "openai" or "ollama") continue;

                var http = httpClientFactory.CreateClient($"provider:{config.Id}");
                http.Timeout = TimeSpan.FromSeconds(config.Timeout);

                builder.AddProvider(config.Id, () => new OpenAiCompatibleLlmClient(
                    http,
                    config,
                    new ConfigAuthResolver(authStore, config.Id),
                    modelCatalog,
                    loggerFactory.CreateLogger<OpenAiCompatibleLlmClient>()));
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Failed to load embedded provider '{name}': {ex.Message}");
            }
        }
    }

    private static void RegisterJsonProvider(
        string file,
        IProviderRegistryBuilder builder,
        IHttpClientFactory httpClientFactory,
        AuthStore authStore,
        DynamicModelCatalog modelCatalog,
        ILoggerFactory loggerFactory)
    {
        try
        {
            var config = ProviderConfig.LoadFromFile(file);
            if (config.IsFailure)
            {
                return;
            }
            if (config.Value.Id is "anthropic" or "openai" or "ollama")
            {
                return;
            }

            var http = httpClientFactory.CreateClient($"provider:{config.Value.Id}");
            http.Timeout = TimeSpan.FromSeconds(config.Value.Timeout);

            builder.AddProvider(config.Value.Id, () => new OpenAiCompatibleLlmClient(
                http,
                config.Value,
                new ConfigAuthResolver(authStore, config.Value.Id),
                modelCatalog,
                loggerFactory.CreateLogger<OpenAiCompatibleLlmClient>()));
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Failed to register provider: {ex.Message}");
        }
    }

    private static IEnumerable<(string Name, string Content)> LoadEmbeddedProviders()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var resourceNames = assembly.GetManifestResourceNames()
            .Where(n => n.Contains("providers.") && n.EndsWith(".json"));

        foreach (string name in resourceNames)
        {
            using var stream = assembly.GetManifestResourceStream(name);
            if (stream is null) continue;
            using var reader = new StreamReader(stream);
            string content = reader.ReadToEnd();
            string shortName = name.Substring(name.LastIndexOf("providers.", StringComparison.Ordinal) + "providers.".Length);
            yield return (shortName, content);
        }
    }

    private static async Task<int> RunInteractiveAsync(params string[] args)
    {
        using var host = BuildHost(args);
        var sp = host.Services;

        var configStore = sp.GetRequiredService<IConfigStore>();
        var authStore = sp.GetRequiredService<AuthStore>();
        var wizard = sp.GetRequiredService<OnboardingWizard>();
        var renderer = sp.GetRequiredService<ITuiRenderer>();
        var eventBus = sp.GetRequiredService<IEventBus>();
        var agent = sp.GetRequiredService<IAgent>();
        var sessionStore = sp.GetRequiredService<ISessionStore>();
        var agentRegistry = sp.GetRequiredService<IAgentRegistry>();
        var providers = sp.GetRequiredService<IProviderRegistry>();

        await renderer.InitializeAsync().ConfigureAwait(false);
        eventBus.Subscribe(async (evt, ct) => await renderer.RenderAsync(evt, ct).ConfigureAwait(false));

        // First-run onboarding check
        var configResult = await configStore.LoadAsync().ConfigureAwait(false);
        var config = configResult.IsSuccess ? configResult.Value : HarborConfig.Default;

        if (!config.Onboarded)
        {
            var writer = (Action<string>)(msg => renderer.WriteLineAsync(msg).GetAwaiter().GetResult());
            var reader = (Func<string, Task<string>>)(async prompt =>
            {
                var result = await renderer.ReadLineAsync(prompt).ConfigureAwait(false);
                return result.IsSuccess ? result.Value : string.Empty;
            });

            var wizardResult = await wizard.RunAsync(reader, writer).ConfigureAwait(false);
            if (wizardResult.IsFailure)
            {
                await renderer.WriteLineAsync($"Setup failed: {wizardResult.Error}").ConfigureAwait(false);
                await renderer.WriteLineAsync("Run `harbor setup` to try again.").ConfigureAwait(false);
                return 1;
            }
            // Reload config
            config = (await configStore.LoadAsync().ConfigureAwait(false)).Value;
        }

        // Show banner — full-screen renderers draw their own header, so skip the text banner.
        if (renderer is not IInteractiveTuiRenderer)
        {
            await renderer.WriteLineAsync("Harbor — modular AI coding agent").ConfigureAwait(false);
            await renderer.WriteLineAsync($"Provider: {config.Provider} | Model: {config.Model} | Agent: {config.Agent}").ConfigureAwait(false);
            await renderer.WriteLineAsync("Type your message and press Enter. Type '/help' for commands, '/exit' to quit.").ConfigureAwait(false);
            await renderer.WriteLineAsync(string.Empty).ConfigureAwait(false);
        }

        // Create session
        string cwd = Environment.CurrentDirectory;
        var defaultAgent = agentRegistry.GetAllAgents().FirstOrDefault(a => a.Name.Value == config.Agent)
                           ?? agentRegistry.GetAllAgents()[0];
        string[] parts = config.Model.Split('/', 2);

        var sessionResult = await sessionStore.CreateAsync(
            cwd, defaultAgent.Name.Value, parts[0],
            parts.Length > 1 ? parts[1] : config.Model).ConfigureAwait(false);

        if (sessionResult.IsFailure)
        {
            await renderer.WriteLineAsync($"Failed to create session: {sessionResult.Error}").ConfigureAwait(false);
            return 1;
        }

        agent.Initialize(sessionResult.Value, defaultAgent);

        // Delegate the interactive loop to the renderer when it owns the screen
        // (e.g. full-screen TUI). Otherwise fall back to the line-buffered REPL.
        if (renderer is IInteractiveTuiRenderer interactive)
        {
            interactive.SetSlashHandler(raw => HandleSlashCommandAsync(
                raw, host.Services, renderer, agent, agentRegistry, configStore, authStore, providers, sessionResult.Value));
            return await interactive.RunInteractiveAsync(agent, host.Services, ct: default).ConfigureAwait(false);
        }

        return await RunReplAsync(renderer, agent, host.Services, configStore, authStore, providers,
            agentRegistry, sessionResult.Value).ConfigureAwait(false);
    }

    private static async Task<int> RunReplAsync(
        ITuiRenderer renderer, IAgent agent, IServiceProvider sp,
        IConfigStore configStore, AuthStore authStore, IProviderRegistry providers,
        IAgentRegistry agentRegistry, Session session)
    {
        while (true)
        {
            var inputResult = await renderer.ReadLineAsync("> ").ConfigureAwait(false);
            if (inputResult.IsFailure) break;
            string? input = inputResult.Value;

            if (string.IsNullOrWhiteSpace(input)) continue;

            string trimmed = input.Trim();
            if (trimmed is "exit" or "quit" or ":q") break;

            if (trimmed.StartsWith('/'))
            {
                await HandleSlashCommandAsync(trimmed, sp, renderer, agent, agentRegistry, configStore, authStore, providers, session).ConfigureAwait(false);
                continue;
            }

            var promptResult = await agent.PromptAsync(trimmed).ConfigureAwait(false);
            if (promptResult.IsFailure)
            {
                await renderer.WriteLineAsync($"Error: {promptResult.Error}").ConfigureAwait(false);
            }
        }

        return 0;
    }

    internal static async Task HandleSlashCommandAsync(
        string input, IServiceProvider sp, ITuiRenderer renderer,
        IAgent agent, IAgentRegistry agentRegistry,
        IConfigStore configStore, AuthStore authStore,
        IProviderRegistry providers,
        Session session)
    {
        string[] parts = input[1..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return;

        string cmd = parts[0].ToLowerInvariant();
        string[] args = parts.Skip(1).ToArray();
        var writer = (Action<string>)(msg => renderer.WriteLineAsync(msg).GetAwaiter().GetResult());
        var reader = (Func<string, Task<string>>)(async prompt =>
        {
            var result = await renderer.ReadLineAsync(prompt).ConfigureAwait(false);
            return result.IsSuccess ? result.Value : string.Empty;
        });

        switch (cmd)
        {
            case "help":
                writer("Commands:");
                writer("  /setup          Run setup wizard");
                writer("  /auth           Manage API keys (/auth set <provider> <key>)");
                writer("  /model <p/m>    Switch model (/model list for options)");
                writer("  /agent <name>   Switch agent (code/plan/explore)");
                writer("  /config         Show/edit config");
                writer("  /providers      List providers");
                writer("  /sessions       List sessions");
                writer("  /tui            Show TUI options");
                writer("  /storage        Show storage options");
                writer("  /exit           Quit");
                break;

            case "setup":
            {
                var wizard = sp.GetRequiredService<OnboardingWizard>();
                await wizard.RunAsync(reader, writer);
                break;
            }

            case "auth":
            {
                var cmd_ = new AuthCommand(authStore, writer);
                var ctx = new SimpleCommandContext(session, agent, providers, sp.GetRequiredService<IToolRegistry>(), writer, reader);
                await cmd_.ExecuteAsync(args, ctx);
                break;
            }

            case "model":
            {
                var cmd_ = new ModelCommand(configStore, providers, writer);
                var ctx = new SimpleCommandContext(session, agent, providers, sp.GetRequiredService<IToolRegistry>(), writer, reader);
                await cmd_.ExecuteAsync(args, ctx);
                break;
            }

            case "agent" or "mode":
            {
                var cmd_ = new AgentCommand(configStore, agentRegistry, writer);
                var ctx = new SimpleCommandContext(session, agent, providers, sp.GetRequiredService<IToolRegistry>(), writer, reader);
                await cmd_.ExecuteAsync(args, ctx);
                break;
            }

            case "config":
            {
                var cmd_ = new ConfigCommand(configStore, writer);
                var ctx = new SimpleCommandContext(session, agent, providers, sp.GetRequiredService<IToolRegistry>(), writer, reader);
                await cmd_.ExecuteAsync(args, ctx);
                break;
            }

            case "providers":
                await RunListProvidersAsync();
                break;

            case "sessions":
                await RunListSessionsAsync();
                break;

            case "tui":
                PrintTuiOptions();
                break;

            case "storage":
                PrintStorageOptions();
                break;

            case "exit" or "quit":
                Environment.Exit(0);
                break;

            default:
                writer($"Unknown command: /{cmd}. Type /help for commands.");
                break;
        }
    }

    private static async Task<int> RunSetupAsync()
    {
        using var host = BuildHost();
        var wizard = host.Services.GetRequiredService<OnboardingWizard>();
        var renderer = host.Services.GetRequiredService<ITuiRenderer>();
        await renderer.InitializeAsync().ConfigureAwait(false);

        var writer = (Action<string>)(msg => renderer.WriteLineAsync(msg).GetAwaiter().GetResult());
        var reader = (Func<string, Task<string>>)(async prompt =>
        {
            var result = await renderer.ReadLineAsync(prompt).ConfigureAwait(false);
            return result.IsSuccess ? result.Value : string.Empty;
        });

        var result = await wizard.RunAsync(reader, writer).ConfigureAwait(false);
        return result.IsSuccess ? 0 : 1;
    }

    private static async Task<int> RunAuthAsync(string[] args)
    {
        using var host = BuildHost(args);
        var authStore = host.Services.GetRequiredService<AuthStore>();

        var writer = (Action<string>)Console.WriteLine;
        var cmd = new AuthCommand(authStore, writer);
        var ctx = new SimpleCommandContext(null!, null!, host.Services.GetRequiredService<IProviderRegistry>(), host.Services.GetRequiredService<IToolRegistry>(), writer, _ => Task.FromResult(string.Empty));
        await cmd.ExecuteAsync(args, ctx);
        return 0;
    }

    private static async Task<int> RunConfigAsync(string[] args)
    {
        using var host = BuildHost(args);
        var configStore = host.Services.GetRequiredService<IConfigStore>();
        var writer = (Action<string>)Console.WriteLine;
        var cmd = new ConfigCommand(configStore, writer);
        var ctx = new SimpleCommandContext(null!, null!, host.Services.GetRequiredService<IProviderRegistry>(), host.Services.GetRequiredService<IToolRegistry>(), writer, _ => Task.FromResult(string.Empty));
        await cmd.ExecuteAsync(args, ctx);
        return 0;
    }

    private static async Task<int> RunAskAsync(string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine("Usage: harbor ask <prompt>");
            return 1;
        }

        string prompt = string.Join(' ', StripLogArgs(args));
        using var host = BuildHost(args);
        var sp = host.Services;

        var renderer = sp.GetRequiredService<ITuiRenderer>();
        var eventBus = sp.GetRequiredService<IEventBus>();
        var agent = sp.GetRequiredService<IAgent>();
        var sessionStore = sp.GetRequiredService<ISessionStore>();
        var agentRegistry = sp.GetRequiredService<IAgentRegistry>();
        var configStore = sp.GetRequiredService<IConfigStore>();

        await renderer.InitializeAsync().ConfigureAwait(false);
        eventBus.Subscribe(async (evt, ct) => await renderer.RenderAsync(evt, ct).ConfigureAwait(false));

        var config = (await configStore.LoadAsync().ConfigureAwait(false)).Value;
        var defaultAgent = agentRegistry.GetAllAgents().FirstOrDefault(a => a.Name.Value == config.Agent)
                           ?? agentRegistry.GetAllAgents()[0];
        string[] parts = config.Model.Split('/', 2);

        var sessionResult = await sessionStore.CreateAsync(Environment.CurrentDirectory, defaultAgent.Name.Value, parts[0], parts.Length > 1 ? parts[1] : config.Model).ConfigureAwait(false);
        if (sessionResult.IsFailure)
        {
            Console.Error.WriteLine($"Failed: {sessionResult.Error}");
            Console.Error.WriteLine("Run `harbor setup` to configure.");
            return 1;
        }

        agent.Initialize(sessionResult.Value, defaultAgent);
        var promptResult = await agent.PromptAsync(prompt).ConfigureAwait(false);
        if (promptResult.IsFailure)
        {
            Console.Error.WriteLine($"Error: {promptResult.Error}");
            return 1;
        }
        return 0;
    }

    private static async Task<int> RunListProvidersAsync()
    {
        using var host = BuildHost();
        var providers = host.Services.GetRequiredService<IProviderRegistry>();
        var providerIds = providers.GetRegisteredProviderIds();

        Console.WriteLine($"Registered providers ({providerIds.Count}):");
        foreach (var id in providerIds)
        {
            var clientResult = providers.GetClient(id);
            string status = clientResult.IsSuccess ? "OK" : "FAIL";
            Console.WriteLine($"  [{status}] {id}");
        }
        await Task.CompletedTask;
        return 0;
    }

    private static async Task<int> RunListModelsAsync(string? providerId)
    {
        using var host = BuildHost();
        var providers = host.Services.GetRequiredService<IProviderRegistry>();

        if (!string.IsNullOrEmpty(providerId))
        {
            var pidResult = ProviderId.TryCreate(providerId);
            if (pidResult.IsFailure)
            {
                Console.Error.WriteLine(pidResult.Error);
                return 1;
            }

            var clientResult = providers.GetClient(pidResult.Value);
            if (clientResult.IsFailure)
            {
                Console.Error.WriteLine(clientResult.Error);
                return 1;
            }

            var modelsResult = await clientResult.Value.GetModelsAsync().ConfigureAwait(false);
            if (modelsResult.IsFailure)
            {
                Console.Error.WriteLine(modelsResult.Error);
                return 1;
            }

            Console.WriteLine($"Models for {providerId} ({modelsResult.Value.Count}):");
            foreach (var m in modelsResult.Value)
                Console.WriteLine($"  {m.Id} — {m.DisplayName}");
            return 0;
        }

        var allResult = await providers.GetAllModelsAsync().ConfigureAwait(false);
        if (allResult.IsFailure)
        {
            Console.Error.WriteLine(allResult.Error);
            return 1;
        }

        Console.WriteLine($"All models ({allResult.Value.Count}):");
        foreach (var g in allResult.Value.GroupBy(m => m.ProviderId))
        {
            Console.WriteLine($"\n{g.Key}:");
            foreach (var m in g) Console.WriteLine($"  {m.Id} — {m.DisplayName}");
        }
        return 0;
    }

    private static async Task<int> RunListSessionsAsync()
    {
        using var host = BuildHost();
        var sessionStore = host.Services.GetRequiredService<ISessionStore>();
        var result = await sessionStore.ListAsync().ConfigureAwait(false);
        if (result.IsFailure)
        {
            Console.Error.WriteLine(result.Error);
            return 1;
        }

        Console.WriteLine($"Sessions ({result.Value.Count}):");
        foreach (var s in result.Value)
            Console.WriteLine($"  {s.Id} — {s.Title} ({s.UpdatedAt:yyyy-MM-dd HH:mm}) [{s.ProviderId}/{s.Model}]");
        return 0;
    }

    private static int PrintTuiOptions()
    {
        Console.WriteLine("""
                          Available TUI renderers (set via /config set tui <name> or HARBOR_TUI env var):
                            ansi      — Default. ANSI escape codes, streaming render.
                            plain     — No ANSI, no colors. For pipes, CI, accessibility.
                            spectre   — Spectre.Console rich rendering (panels, tables, markup).
                            fullscreen— Full-screen Spectre TUI: panels, live layout, inline input.
                          """);
        return 0;
    }

    private static int PrintStorageOptions()
    {
        Console.WriteLine("""
                          Available storage backends (set via /config set storage <name> or HARBOR_STORAGE env var):
                            jsonl   — Default. Append-only JSONL files. No native deps.
                            memory  — In-memory. Lost on exit. For tests/ephemeral.
                            sqlite  — SQLite DB with indexed queries. Native e_sqlite3 dep.
                          """);
        return 0;
    }

    private static int PrintHelp()
    {
        Console.WriteLine("""
                          Harbor — modular AI coding agent.

                          Usage:
                            harbor                  Start interactive TUI REPL (runs setup on first run)
                            harbor ask <prompt>     Run one-shot prompt
                            harbor setup            Run setup wizard
                            harbor auth set <p> <k> Set API key for provider
                            harbor auth list        List configured API keys
                            harbor providers        List registered providers
                            harbor models [pid]     List available models
                            harbor sessions         List saved sessions
                            harbor config           Show configuration
                            harbor tui              Show TUI options
                            harbor storage          Show storage options
                            harbor help             Show this help
                            harbor version          Show version

                          First run:
                            harbor setup
                            → Pick provider (kilocode has FREE models)
                            → Enter API key (or skip for local providers)
                            → Pick model and agent

                          Slash commands (in REPL):
                            /setup, /auth, /model, /agent, /config, /providers, /sessions, /tui, /storage, /help, /exit
                          """);
        return 0;
    }

    private static int PrintVersion()
    {
        Console.WriteLine("Harbor v0.3.0-alpha");
        Console.WriteLine(".NET " + Environment.Version);
        Console.WriteLine($"OS: {Environment.OSVersion}");
        return 0;
    }

    private static string? FindProvidersDirectory()
    {
        string exeDir = AppContext.BaseDirectory;
        string providersInExe = Path.Combine(exeDir, "providers");
        if (Directory.Exists(providersInExe)) return providersInExe;

        string? current = exeDir;
        for (int i = 0; i < 8 && current is not null; i++)
        {
            string candidate = Path.Combine(current, "providers");
            if (Directory.Exists(candidate)) return candidate;
            current = Path.GetDirectoryName(current);
        }

        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string userProviders = Path.Combine(home, ".harbor", "providers");
        return Directory.Exists(userProviders) ? userProviders : null;
    }
}

/// <summary>
///     Adapter that resolves API key via AuthStore (config file first, then env var).
/// </summary>
internal sealed class ConfigAuthResolver : IAnthropicAuthResolver,
    IOpenAIAuthResolver,
    IAuthResolver
{
    private readonly AuthStore _authStore;
    private readonly string _providerId;

    public ConfigAuthResolver(AuthStore authStore, string providerId)
    {
        _authStore = authStore;
        _providerId = providerId;
    }

    // For native providers (Anthropic, OpenAI) — no providerId arg
    public Task<Result<string>> ResolveApiKeyAsync(CancellationToken ct = default)
        => _authStore.GetApiKeyAsync(_providerId, ct);

    // For OpenAiCompatible.IAuthResolver — takes providerId arg
    public Task<Result<string>> ResolveApiKeyAsync(string providerId, CancellationToken ct = default)
        => _authStore.GetApiKeyAsync(string.IsNullOrEmpty(providerId) ? _providerId : providerId, ct);
}

/// <summary>
///     Simple ICommandContext implementation for REPL.
/// </summary>
internal sealed class SimpleCommandContext : ICommandContext
{
    public SimpleCommandContext(
        Session session,
        IAgent agent,
        IProviderRegistry providers,
        IToolRegistry tools,
        Action<string> output,
        Func<string, Task<string>> prompt)
    {
        Session = new DummySessionContext(session);
        Agent = agent;
        Providers = providers;
        Tools = tools;
        Output = output;
        Prompt = prompt;
    }

    public ISessionContext Session { get; }
    public IAgent Agent { get; }
    public IProviderRegistry Providers { get; }
    public IToolRegistry Tools { get; }
    public Action<string> Output { get; }
    public Func<string, Task<string>> Prompt { get; }
}

internal sealed class DummySessionContext : ISessionContext
{
    public DummySessionContext(Session session) { Session = session; }
    public Session Session { get; }
    public IReadOnlyList<AgentMessage> Messages => Array.Empty<AgentMessage>();
    public Channel<AgentMessage> SteeringQueue => Channel.CreateUnbounded<AgentMessage>();
    public Task AppendMessageAsync(AgentMessage message, CancellationToken ct = default) => Task.CompletedTask;
    public Task UpdateStatsAsync(Usage usage, CancellationToken ct = default) => Task.CompletedTask;
}
