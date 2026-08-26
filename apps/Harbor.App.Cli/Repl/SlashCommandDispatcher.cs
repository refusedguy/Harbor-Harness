using Harbor.Abstractions.Agents;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Providers;
using Harbor.Abstractions.Sessions;
using Harbor.Abstractions.Tools;
using Harbor.Abstractions.Tui;
using Harbor.App.Cli.Commands;
using Harbor.App.Cli.Hosting;
using Harbor.Application.Configuration;
using Harbor.Application.Onboarding;
using Harbor.Terminal.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
namespace Harbor.App.Cli.Repl;
/// <summary>
///     Slash command dispatcher — single responsibility: route /commands to handlers.
///     Extracted from Program.cs.
/// </summary>
internal sealed class SlashCommandDispatcher
{
    private readonly ILogger<SlashCommandDispatcher> _logger;

    public SlashCommandDispatcher(ILogger<SlashCommandDispatcher> logger)
    {
        _logger = logger;
    }

    public async Task<SlashCommandOutcome> HandleAsync(
        string input, IServiceProvider sp, ITuiRenderer renderer,
        IAgent agent, IAgentRegistry agentRegistry,
        IConfigStore configStore, AuthStore authStore,
        IProviderRegistry providers, Session session)
    {
        // The legacy renderer path funnels its output through ITuiRenderer;
        // the delegates below keep that contract in one place.
        return await HandleCoreAsync(input, sp,
            writer: msg => _ = renderer.WriteLineAsync(msg),
            reader: async prompt =>
            {
                var r = await renderer.ReadLineAsync(prompt).ConfigureAwait(false);
                return r.IsSuccess ? r.Value : string.Empty;
            },
            agent, agentRegistry, configStore, authStore, providers, session).ConfigureAwait(false);
    }

    /// <summary>
    ///     CE-4: renderer-free overload for the ConsoleEx REPL — output goes to
    ///     the chat timeline and input comes from the composer instead of an
    ///     <see cref="ITuiRenderer" />.
    /// </summary>
    public Task<SlashCommandOutcome> HandleCoreAsync(
        string input, IServiceProvider sp,
        Action<string> writer, Func<string, Task<string>> reader,
        IAgent agent, IAgentRegistry agentRegistry,
        IConfigStore configStore, AuthStore authStore,
        IProviderRegistry providers, Session session)
    {
        string[] parts = input[1..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return Task.FromResult(SlashCommandOutcome.Continue);

        return HandleKnownCommandAsync(parts, sp, writer, reader,
            agent, agentRegistry, configStore, authStore, providers, session);
    }

    private async Task<SlashCommandOutcome> HandleKnownCommandAsync(
        string[] parts, IServiceProvider sp,
        Action<string> writer, Func<string, Task<string>> reader,
        IAgent agent, IAgentRegistry agentRegistry,
        IConfigStore configStore, AuthStore authStore,
        IProviderRegistry providers, Session session)
    {
        string cmd = parts[0].ToLowerInvariant();
        string[] args = parts.Skip(1).ToArray();
        _logger.LogInformation("Slash command: /{Command} args={ArgCount}", cmd, args.Length);

        // Quit commands are resolved before any dependency is touched so the
        // shutdown decision never depends on renderer/DI state. Returning the
        // outcome lets the caller run its normal cleanup (IPC stop, host
        // dispose) — no Environment.Exit anywhere.
        if (cmd is "exit" or "quit")
        {
            _logger.LogInformation("Quit requested via /{Command}", cmd);
            return SlashCommandOutcome.Quit(0);
        }

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
                    // PROD-UI-0 З.3: pass the live agent + session so /model
                    // rebinds the active session (no REPL restart).
                    await new ModelCommand(configStore, providers, writer, agent, session).ExecuteAsync(args, MakeCtx(session, agent, providers, sp, writer, reader));
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
                default:
                    _logger.LogWarning("Unknown command: /{Command}", cmd);
                    writer($"Unknown: /{cmd}. /help for commands.");
                    break;
            }
            _logger.LogDebug("Command /{Command} completed", cmd);
            return SlashCommandOutcome.Continue;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error dispatching command /{Command}", cmd);
            writer($"Error: {ex.Message}");
            return SlashCommandOutcome.Continue;
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

    public static async Task<int?> TryHandleAsync(string commandName, string[] args, ICommand[] commands, CancellationToken ct = default)
    {
        var command = commands.FirstOrDefault(c => c.Name.Equals(commandName, StringComparison.OrdinalIgnoreCase));
        if (command is null)
        {
            return null;
        }

        // Thread the command's own result through: 0 = success, non-zero =
        // failure. The caller maps it directly onto the process exit code.
        return await command.ExecuteAsync(args, ct).ConfigureAwait(false);
    }
}
