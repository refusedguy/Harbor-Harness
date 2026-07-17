using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Harbor.Tui.Abstractions;
using Harbor.Abstractions.Agents;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Providers;
using Harbor.Abstractions.Sessions;
using Harbor.Abstractions.Tools;
using Harbor.Abstractions.Tui;
using Harbor.Core.Configuration;
using Harbor.Core.Onboarding;
using Harbor.Cli.Commands;
using Harbor.Cli.Hosting;

namespace Harbor.Cli.Repl;

/// <summary>
/// Slash command dispatcher — single responsibility: route /commands to handlers.
/// Extracted from Program.cs.
/// </summary>
internal sealed class SlashCommandDispatcher
{
    private readonly ILogger<SlashCommandDispatcher> _logger;

    public SlashCommandDispatcher(ILogger<SlashCommandDispatcher> logger)
    {
        _logger = logger;
    }

    public async Task HandleAsync(
        string input, IServiceProvider sp, ITuiRenderer renderer,
        IAgent agent, IAgentRegistry agentRegistry,
        IConfigStore configStore, AuthStore authStore,
        IProviderRegistry providers, Session session)
    {
        string[] parts = input[1..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return;

        string cmd = parts[0].ToLowerInvariant();
        string[] args = parts.Skip(1).ToArray();
        _logger.LogInformation("Slash command: /{Command} args={ArgCount}", cmd, args.Length);
        var writer = (Action<string>)(msg => _ = renderer.WriteLineAsync(msg));
        var reader = (Func<string, Task<string>>)(async prompt =>
        {
            var r = await renderer.ReadLineAsync(prompt).ConfigureAwait(false);
            return r.IsSuccess ? r.Value : string.Empty;
        });

        try
        {
            switch (cmd)
            {
                case "help":
                    writer("Commands: /setup /auth /model /agent /config /providers /sessions /tui /storage /exit");
                    break;
                case "setup":
                    await sp.GetRequiredService<OnboardingWizard>().RunAsync(reader, writer);
                    break;
                case "auth":
                    await new AuthCommand(authStore, writer).ExecuteAsync(args, MakeCtx(session, agent, providers, sp, writer, reader));
                    break;
                case "model":
                    await new ModelCommand(configStore, providers, writer).ExecuteAsync(args, MakeCtx(session, agent, providers, sp, writer, reader));
                    break;
                case "agent" or "mode":
                    await new AgentCommand(configStore, agentRegistry, writer).ExecuteAsync(args, MakeCtx(session, agent, providers, sp, writer, reader));
                    break;
                case "config":
                    await new ConfigCommand(configStore, writer).ExecuteAsync(args, MakeCtx(session, agent, providers, sp, writer, reader));
                    break;
                case "providers": await ListProviders(sp); break;
                case "sessions": await ListSessions(sp); break;
                case "tui": PrintTuiOptions(); break;
                case "storage": PrintStorageOptions(); break;
                case "exit" or "quit": Environment.Exit(0); break;
                default: _logger.LogWarning("Unknown command: /{Command}", cmd); writer($"Unknown: /{cmd}. /help for commands."); break;
            }
            _logger.LogDebug("Command /{Command} completed", cmd);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error dispatching command /{Command}", cmd);
            writer($"Error: {ex.Message}");
        }
    }

    private static ICommandContext MakeCtx(Session session, IAgent agent, IProviderRegistry providers,
        IServiceProvider sp, Action<string> writer, Func<string, Task<string>> reader) =>
        new SimpleCommandContext(session, agent, providers, sp.GetRequiredService<IToolRegistry>(), writer, reader);

    private static async Task ListProviders(IServiceProvider sp)
    {
        var providers = sp.GetRequiredService<IProviderRegistry>();
        Console.WriteLine($"Providers ({providers.GetRegisteredProviderIds().Count}):");
        foreach (var id in providers.GetRegisteredProviderIds())
        {
            var r = providers.GetClient(id);
            Console.WriteLine($"  [{(r.IsSuccess ? "OK" : "FAIL")}] {id}");
        }
        await Task.CompletedTask;
    }

    private static async Task ListSessions(IServiceProvider sp)
    {
        var store = sp.GetRequiredService<ISessionStore>();
        var result = await store.ListAsync().ConfigureAwait(false);
        if (result.IsSuccess)
            foreach (var s in result.Value)
                Console.WriteLine($"  {s.Id} — {s.Title} [{s.ProviderId}/{s.Model}]");
    }

    private static void PrintTuiOptions() => Console.WriteLine("TUI: ansi (default), plain, spectre, fullscreen");
    private static void PrintStorageOptions() => Console.WriteLine("Storage: jsonl (default), memory, sqlite");
}
