using Harbor.Abstractions.Models.Identifiers;
using Harbor.Abstractions.Providers;
using Harbor.Abstractions.Sessions;
using Harbor.Abstractions.Tools;
using Harbor.Cli.Commands;
using Harbor.Cli.Hosting;
using Harbor.Cli.Logging;
using Harbor.Cli.Repl;
using Harbor.Core.Configuration;
using Harbor.Core.Onboarding;
using Harbor.Tui.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
namespace Harbor.Cli;
/// <summary>
///     Entry point — thin dispatcher. All logic delegated to HostBuilder, ReplRunner, SlashCommandDispatcher.
/// </summary>
public static class Program
{
    private static ILogger _logger = null!;

    public static async Task<int> Main(string[] args)
    {
        var logLevel = ResolveLogLevel(args);
        var loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.AddProvider(new FileLoggerProvider(logLevel));
            if (logLevel <= LogLevel.Information)
            {
                builder.AddSimpleConsole(o =>
                {
                    o.SingleLine = true;
                    o.TimestampFormat = "HH:mm:ss ";
                });
            }
            builder.SetMinimumLevel(logLevel);
        });
        _logger = loggerFactory.CreateLogger(typeof(Program).FullName ?? "Program");

        _logger.LogInformation("Starting Harbor CLI with {ArgCount} args: {Args}", args.Length, string.Join(' ', args));
        try
        {
            if (args.Length == 0)
            {
                _logger.LogInformation("No args provided — entering interactive mode");
                return await RunInteractiveAsync(args);
            }

            string command = args[0].ToLowerInvariant();
            _logger.LogInformation("Command: {Command}", command);
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
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled exception in CLI entry point");
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    private static async Task<int> RunInteractiveAsync(params string[] args)
    {
        _logger.LogInformation("Starting interactive mode");
        using var host = HostBuilder.Build(args);
        var runner = new ReplRunner(host.Services.GetRequiredService<ILogger<ReplRunner>>());
        int exitCode = await runner.RunInteractiveAsync(host.Services).ConfigureAwait(false);
        _logger.LogInformation("Interactive mode ended with exit code {ExitCode}", exitCode);
        return exitCode;
    }

    private static async Task<int> RunAskAsync(string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine("Usage: harbor ask <prompt>");
            return 1;
        }
        string prompt = string.Join(' ', StripLogArgs(args));
        _logger.LogInformation("Starting ask command with prompt length {Length}", prompt.Length);
        using var host = HostBuilder.Build(args);
        var runner = new ReplRunner(host.Services.GetRequiredService<ILogger<ReplRunner>>());
        return await runner.RunAskAsync(host.Services, prompt).ConfigureAwait(false);
    }

    private static async Task<int> RunSetupAsync()
    {
        _logger.LogInformation("Starting setup wizard");
        using var host = HostBuilder.Build();
        var wizard = host.Services.GetRequiredService<OnboardingWizard>();
        var renderer = host.Services.GetRequiredService<ITuiRenderer>();
        await renderer.InitializeAsync().ConfigureAwait(false);
        var writer = (Action<string>)(msg => _ = renderer.WriteLineAsync(msg));
        var reader = (Func<string, Task<string>>)(async prompt =>
        {
            var r = await renderer.ReadLineAsync(prompt).ConfigureAwait(false);
            return r.IsSuccess ? r.Value : string.Empty;
        });
        var result = await wizard.RunAsync(reader, writer).ConfigureAwait(false);
        _logger.LogInformation("Setup wizard finished with success={Success}", result.IsSuccess);
        return result.IsSuccess ? 0 : 1;
    }

    private static async Task<int> RunAuthAsync(string[] args)
    {
        _logger.LogInformation("Starting auth command");
        using var host = HostBuilder.Build(args);
        var authStore = host.Services.GetRequiredService<AuthStore>();
        var writer = (Action<string>)Console.WriteLine;
        var cmd = new AuthCommand(authStore, writer);
        var ctx = new SimpleCommandContext(null!, null!,
            host.Services.GetRequiredService<IProviderRegistry>(),
            host.Services.GetRequiredService<IToolRegistry>(),
            writer, _ => Task.FromResult(string.Empty));
        await cmd.ExecuteAsync(args, ctx);
        return 0;
    }

    private static async Task<int> RunConfigAsync(string[] args)
    {
        _logger.LogInformation("Starting config command");
        using var host = HostBuilder.Build(args);
        var configStore = host.Services.GetRequiredService<IConfigStore>();
        var writer = (Action<string>)Console.WriteLine;
        var cmd = new ConfigCommand(configStore, writer);
        var ctx = new SimpleCommandContext(null!, null!,
            host.Services.GetRequiredService<IProviderRegistry>(),
            host.Services.GetRequiredService<IToolRegistry>(),
            writer, _ => Task.FromResult(string.Empty));
        await cmd.ExecuteAsync(args, ctx);
        return 0;
    }

    private static async Task<int> RunListProvidersAsync()
    {
        _logger.LogInformation("Listing providers");
        using var host = HostBuilder.Build();
        var providers = host.Services.GetRequiredService<IProviderRegistry>();
        var ids = providers.GetRegisteredProviderIds();
        _logger.LogInformation("Found {Count} registered providers", ids.Count);
        Console.WriteLine($"Providers ({ids.Count}):");
        foreach (var id in ids)
        {
            var r = providers.GetClient(id);
            Console.WriteLine($"  [{(r.IsSuccess ? "OK" : "FAIL")}] {id}");
        }
        await Task.CompletedTask;
        return 0;
    }

    private static async Task<int> RunListModelsAsync(string? providerId)
    {
        _logger.LogInformation("Listing models for provider {Provider}", providerId ?? "(all)");
        using var host = HostBuilder.Build();
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
            _logger.LogInformation("Found {Count} models for {Provider}", modelsResult.Value.Count, providerId);
            Console.WriteLine($"Models for {providerId}:");
            foreach (var m in modelsResult.Value) Console.WriteLine($"  {m.Id} — {m.DisplayName}");
            return 0;
        }
        var allResult = await providers.GetAllModelsAsync().ConfigureAwait(false);
        if (allResult.IsFailure)
        {
            Console.Error.WriteLine(allResult.Error);
            return 1;
        }
        _logger.LogInformation("Found {Count} total models", allResult.Value.Count);
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
        _logger.LogInformation("Listing sessions");
        using var host = HostBuilder.Build();
        var store = host.Services.GetRequiredService<ISessionStore>();
        var result = await store.ListAsync().ConfigureAwait(false);
        if (result.IsSuccess)
        {
            _logger.LogInformation("Found {Count} sessions", result.Value.Count);
            foreach (var s in result.Value)
                Console.WriteLine($"  {s.Id} — {s.Title} [{s.ProviderId}/{s.Model}]");
        }
        return 0;
    }

    private static int PrintTuiOptions()
    {
        Console.WriteLine("TUI: ansi (default), plain, spectre, fullscreen");
        return 0;
    }
    private static int PrintStorageOptions()
    {
        Console.WriteLine("Storage: jsonl (default), memory, sqlite");
        return 0;
    }

    private static int PrintHelp()
    {
        Console.WriteLine("""
                          Harbor — modular AI coding agent.
                          Usage: harbor [ask <prompt>|setup|auth|config|providers|models|sessions|tui|storage|help|version]
                          """);
        return 0;
    }

    private static int PrintVersion()
    {
        Console.WriteLine("Harbor v0.4.0-alpha");
        Console.WriteLine($".NET {Environment.Version}");
        return 0;
    }

    // ── Helpers ──
    internal static LogLevel ResolveLogLevel(string[] args)
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
        return Enum.TryParse<LogLevel>(raw, true, out var level) ? level : LogLevel.Warning;
    }

    internal static string[] StripLogArgs(string[] args)
    {
        var result = new List<string>(args.Length);
        int i = 0;
        while (i < args.Length)
        {
            if (args[i].Equals("--loglevel", StringComparison.OrdinalIgnoreCase) ||
                args[i].Equals("-ll", StringComparison.OrdinalIgnoreCase))
            {
                i += 2;
                continue;
            }
            result.Add(args[i]);
            i++;
        }
        return result.ToArray();
    }
}
