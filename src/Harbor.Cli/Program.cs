using Microsoft.Extensions.DependencyInjection;
using Harbor.Tui.Abstractions;
using Harbor.Cli.Hosting;
using Harbor.Cli.Repl;
using Microsoft.Extensions.Logging;

namespace Harbor.Cli;

/// <summary>
/// Entry point — thin dispatcher. All logic delegated to HostBuilder, ReplRunner, SlashCommandDispatcher.
/// </summary>
public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Length == 0)
            return await RunInteractiveAsync(args);

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

    private static async Task<int> RunInteractiveAsync(params string[] args)
    {
        using var host = HostBuilder.Build(args);
        return await ReplRunner.RunInteractiveAsync(host.Services).ConfigureAwait(false);
    }

    private static async Task<int> RunAskAsync(string[] args)
    {
        if (args.Length == 0) { Console.Error.WriteLine("Usage: harbor ask <prompt>"); return 1; }
        string prompt = string.Join(' ', StripLogArgs(args));
        using var host = HostBuilder.Build(args);
        return await ReplRunner.RunAskAsync(host.Services, prompt).ConfigureAwait(false);
    }

    private static async Task<int> RunSetupAsync()
    {
        using var host = HostBuilder.Build();
        var wizard = host.Services.GetRequiredService<Core.Onboarding.OnboardingWizard>();
        var renderer = host.Services.GetRequiredService<Tui.Abstractions.ITuiRenderer>();
        await renderer.InitializeAsync().ConfigureAwait(false);
        var writer = (Action<string>)(msg => renderer.WriteLineAsync(msg).GetAwaiter().GetResult());
        var reader = (Func<string, Task<string>>)(async prompt =>
        {
            var r = await renderer.ReadLineAsync(prompt).ConfigureAwait(false);
            return r.IsSuccess ? r.Value : string.Empty;
        });
        var result = await wizard.RunAsync(reader, writer).ConfigureAwait(false);
        return result.IsSuccess ? 0 : 1;
    }

    private static async Task<int> RunAuthAsync(string[] args)
    {
        using var host = HostBuilder.Build(args);
        var authStore = host.Services.GetRequiredService<Core.Configuration.AuthStore>();
        var writer = (Action<string>)Console.WriteLine;
        var cmd = new Commands.AuthCommand(authStore, writer);
        var ctx = new SimpleCommandContext(null!, null!,
            host.Services.GetRequiredService<Abstractions.Providers.IProviderRegistry>(),
            host.Services.GetRequiredService<Abstractions.Tools.IToolRegistry>(),
            writer, _ => Task.FromResult(string.Empty));
        await cmd.ExecuteAsync(args, ctx);
        return 0;
    }

    private static async Task<int> RunConfigAsync(string[] args)
    {
        using var host = HostBuilder.Build(args);
        var configStore = host.Services.GetRequiredService<Core.Configuration.IConfigStore>();
        var writer = (Action<string>)Console.WriteLine;
        var cmd = new Commands.ConfigCommand(configStore, writer);
        var ctx = new SimpleCommandContext(null!, null!,
            host.Services.GetRequiredService<Abstractions.Providers.IProviderRegistry>(),
            host.Services.GetRequiredService<Abstractions.Tools.IToolRegistry>(),
            writer, _ => Task.FromResult(string.Empty));
        await cmd.ExecuteAsync(args, ctx);
        return 0;
    }

    private static async Task<int> RunListProvidersAsync()
    {
        using var host = HostBuilder.Build();
        var providers = host.Services.GetRequiredService<Abstractions.Providers.IProviderRegistry>();
        Console.WriteLine($"Providers ({providers.GetRegisteredProviderIds().Count}):");
        foreach (var id in providers.GetRegisteredProviderIds())
        {
            var r = providers.GetClient(id);
            Console.WriteLine($"  [{(r.IsSuccess ? "OK" : "FAIL")}] {id}");
        }
        await Task.CompletedTask;
        return 0;
    }

    private static async Task<int> RunListModelsAsync(string? providerId)
    {
        using var host = HostBuilder.Build();
        var providers = host.Services.GetRequiredService<Abstractions.Providers.IProviderRegistry>();
        if (!string.IsNullOrEmpty(providerId))
        {
            var pidResult = Abstractions.Models.Identifiers.ProviderId.TryCreate(providerId);
            if (pidResult.IsFailure) { Console.Error.WriteLine(pidResult.Error); return 1; }
            var clientResult = providers.GetClient(pidResult.Value);
            if (clientResult.IsFailure) { Console.Error.WriteLine(clientResult.Error); return 1; }
            var modelsResult = await clientResult.Value.GetModelsAsync().ConfigureAwait(false);
            if (modelsResult.IsFailure) { Console.Error.WriteLine(modelsResult.Error); return 1; }
            Console.WriteLine($"Models for {providerId}:");
            foreach (var m in modelsResult.Value) Console.WriteLine($"  {m.Id} — {m.DisplayName}");
            return 0;
        }
        var allResult = await providers.GetAllModelsAsync().ConfigureAwait(false);
        if (allResult.IsFailure) { Console.Error.WriteLine(allResult.Error); return 1; }
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
        using var host = HostBuilder.Build();
        var store = host.Services.GetRequiredService<Abstractions.Sessions.ISessionStore>();
        var result = await store.ListAsync().ConfigureAwait(false);
        if (result.IsSuccess)
            foreach (var s in result.Value)
                Console.WriteLine($"  {s.Id} — {s.Title} [{s.ProviderId}/{s.Model}]");
        return 0;
    }

    private static int PrintTuiOptions() { Console.WriteLine("TUI: ansi (default), plain, spectre, fullscreen"); return 0; }
    private static int PrintStorageOptions() { Console.WriteLine("Storage: jsonl (default), memory, sqlite"); return 0; }

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
            { raw = args[i + 1]; break; }
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
            { i += 2; continue; }
            result.Add(args[i]); i++;
        }
        return result.ToArray();
    }
}
