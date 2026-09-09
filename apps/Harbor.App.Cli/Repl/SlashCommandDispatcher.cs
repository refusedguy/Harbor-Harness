using System.Collections.Frozen;
using CSharpFunctionalExtensions;
using Harbor.Abstractions.Agents;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Permissions;
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
/// <remarks>
///     Commands are registered in a dictionary keyed by canonical name and aliases.
///     <see cref="GetRegisteredCommands" /> exposes the full list for the command
///     palette; <see cref="GetArgSuggestions" /> supplies the second-step picker.
/// </remarks>
internal sealed class SlashCommandDispatcher
{
    private readonly ILogger<SlashCommandDispatcher> _logger;
    private readonly FrozenDictionary<string, SlashCommandRegistration> _byName;

    /// <summary>All registered slash commands (canonical + aliases → single registration).</summary>
    private sealed record SlashCommandRegistration(
        string CanonicalName,
        IReadOnlyList<string> Aliases,
        IReadOnlyList<string>? ArgSuggestions,
        Func<CommandContext, IReadOnlyList<string>, Task<Result>> Execute);

    /// <summary>Lightweight context bag passed to command execute delegates.</summary>
    public sealed record CommandContext(
        IServiceProvider Services,
        Action<string> Writer,
        Func<string, Task<string>>? Reader,
        Session Session,
        IAgent Agent,
        IAgentRegistry AgentRegistry,
        IProviderRegistry Providers,
        IConfigStore ConfigStore,
        AuthStore AuthStore,
        IToolRegistry ToolRegistry);

    public SlashCommandDispatcher(ILogger<SlashCommandDispatcher> logger)
    {
        _logger = logger;
        _byName = BuildRegistry();
    }

    public async Task<SlashCommandOutcome> HandleAsync(
        string input, IServiceProvider sp, ITuiRenderer renderer,
        IAgent agent, IAgentRegistry agentRegistry,
        IConfigStore configStore, AuthStore authStore,
        IProviderRegistry providers, Session session)
    {
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
    ///     CE-4: renderer-free overload for the CellForge REPL — output goes to
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

        string cmd = parts[0].ToLowerInvariant();
        string[] args = parts.Skip(1).ToArray();

        if (cmd is "exit" or "quit")
        {
            _logger.LogInformation("Quit requested via /{Command}", cmd);
            return Task.FromResult(SlashCommandOutcome.Quit(0));
        }

        if (!_byName.TryGetValue(cmd, out var reg))
        {
            _logger.LogWarning("Unknown command: /{Command}", cmd);
            writer($"Unknown: /{cmd}. /help for commands.");
            return Task.FromResult(SlashCommandOutcome.Continue);
        }

        var ctx = new CommandContext(sp, writer, reader, session, agent, agentRegistry, providers,
            configStore, authStore, sp.GetRequiredService<IToolRegistry>());

        return ExecuteRegisteredAsync(reg, ctx, args);
    }

    /// <summary>All registered slash commands, including aliases.</summary>
    public IReadOnlyList<ISlashCommand> GetRegisteredCommands()
    {
        var list = new List<ISlashCommand>(_byName.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var reg in _byName.Values)
        {
            if (seen.Add(reg.CanonicalName))
            {
                list.Add(new DelegateSlashCommand(reg.CanonicalName, reg.Aliases, reg.ArgSuggestions));
            }
        }

        return list;
    }

    /// <summary>
    ///     Arg suggestions for the named command, or <see langword="null" /> when
    ///     the command takes no arguments or suggestions are not applicable.
    /// </summary>
    public IReadOnlyList<string>? GetArgSuggestions(string commandName)
    {
        if (string.IsNullOrEmpty(commandName))
        {
            return null;
        }

        if (_byName.TryGetValue(commandName.ToLowerInvariant(), out var reg))
        {
            return reg.ArgSuggestions;
        }

        return null;
    }

    private async Task<SlashCommandOutcome> ExecuteRegisteredAsync(
        SlashCommandRegistration reg, CommandContext ctx, IReadOnlyList<string> args)
    {
        try
        {
            _logger.LogInformation("Slash command: /{Command} args={ArgCount}", reg.CanonicalName, args.Count);
            var result = await reg.Execute(ctx, args).ConfigureAwait(false);
            _logger.LogDebug("Command /{Command} completed", reg.CanonicalName);
            return SlashCommandOutcome.Continue;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error dispatching command /{Command}", reg.CanonicalName);
            ctx.Writer($"Error: {ex.Message}");
            return SlashCommandOutcome.Continue;
        }
    }

    private static FrozenDictionary<string, SlashCommandRegistration> BuildRegistry()
    {
        var dict = new Dictionary<string, SlashCommandRegistration>(capacity: 32);

        void Register(
            string canonical,
            IReadOnlyList<string> aliases,
            IReadOnlyList<string>? argSuggestions,
            Func<CommandContext, IReadOnlyList<string>, Task<Result>> execute)
        {
            var reg = new SlashCommandRegistration(canonical, aliases, argSuggestions, execute);
            dict[canonical.ToLowerInvariant()] = reg;
            foreach (var a in aliases)
            {
                dict[a.ToLowerInvariant()] = reg;
            }
        }

        Register("help", ["h"], null, (ctx, _) =>
        {
            ctx.Writer("Commands: /setup /auth /model /agent /config /permissions /providers /sessions /tree /fork /plugins /tui /renderer /storage /exit");
            return Task.FromResult(Result.Success());
        });

        Register("new", ["new-session"], null, (ctx, _) =>
        {
            ctx.Writer("Use /new in the interactive TUI to start a fresh session.");
            return Task.FromResult(Result.Success());
        });

        Register("setup", [], null, async (ctx, _) =>
        {
            var result = await ctx.Services.GetRequiredService<OnboardingWizard>()
                .RunAsync(ctx.Reader!, ctx.Writer).ConfigureAwait(false);
            return result;
        });

        Register("auth", ["key", "api-key"], null, (ctx, _) =>
        {
            return new AuthCommand(ctx.AuthStore, ctx.Writer)
                .ExecuteAsync(Array.Empty<string>(), MakeCtx(ctx));
        });

        Register("model", ["m"], null, (ctx, _) =>
        {
            return new ModelCommand(ctx.ConfigStore, ctx.Providers, ctx.Writer, ctx.Agent, ctx.Session)
                .ExecuteAsync(Array.Empty<string>(), MakeCtx(ctx));
        });

        Register("agent", ["mode", "a"], null, (ctx, _) =>
        {
            return new AgentCommand(ctx.ConfigStore, ctx.AgentRegistry, ctx.Writer)
                .ExecuteAsync(Array.Empty<string>(), MakeCtx(ctx));
        });

        Register("config", [], null, (ctx, _) =>
        {
            return new ConfigCommand(ctx.ConfigStore, ctx.Writer)
                .ExecuteAsync(Array.Empty<string>(), MakeCtx(ctx));
        });

        Register("permissions", [], null, (ctx, _) =>
        {
            return new PermissionsCommand(
                    ctx.Services.GetRequiredService<IPermissionService>(),
                    ctx.Services.GetRequiredService<IAgentRegistry>(),
                    ctx.ConfigStore, ctx.Writer, ctx.Agent, ctx.Session)
                .ExecuteAsync(Array.Empty<string>(), MakeCtx(ctx));
        });

        Register("providers", [], null, async (ctx, _) =>
        {
            var providers = ctx.Services.GetRequiredService<IProviderRegistry>();
            ctx.Writer($"Providers ({providers.GetRegisteredProviderIds().Count}):");
            foreach (var id in providers.GetRegisteredProviderIds())
            {
                var r = providers.GetClient(id);
                ctx.Writer($"  [{(r.IsSuccess ? "OK" : "FAIL")}] {id}");
            }
            await Task.CompletedTask;
            return Result.Success();
        });

        Register("sessions", [], null, async (ctx, _) =>
        {
            var store = ctx.Services.GetRequiredService<ISessionStore>();
            var result = await store.ListAsync().ConfigureAwait(false);
            if (result.IsSuccess)
                foreach (var s in result.Value)
                    ctx.Writer($"  {s.Id} — {s.Title} [{s.ProviderId}/{s.Model}]");
            return Result.Success();
        });

        Register("tree", [], null, static async (ctx, _) =>
        {
            var store = ctx.Services.GetRequiredService<ISessionStore>();
            var built = await SessionTreeRunner.BuildAsync(store, ctx.Session.Id).ConfigureAwait(false);
            if (built.IsFailure)
            {
                ctx.Writer($"Cannot list sessions: {built.Error}");
                return Result.Success();
            }

            if (built.Value.Count == 0)
                ctx.Writer("No sessions.");
            else
                foreach (var line in built.Value)
                    ctx.Writer(line);
            return Result.Success();
        });

        Register("fork", [], null, static async (ctx, args) =>
        {
            if (args.Count < 2)
            {
                ctx.Writer("Usage: /fork <session-id> <message-id>");
                return Result.Success();
            }

            var outcome = await new SessionForkRunner(ctx.Services.GetRequiredService<ISessionStore>())
                .ForkAsync(args[0], args[1]).ConfigureAwait(false);
            if (outcome.IsFailure)
            {
                ctx.Writer($"Fork failed: {outcome.Error}");
                return Result.Success();
            }

            ctx.Writer($"Forked → {outcome.Value.ForkId}: copied {outcome.Value.Copied} message(s).");
            return Result.Success();
        });

        Register("plugins", [], null, (ctx, _) =>
        {
            if (ctx.Services.GetService<Harbor.Hosting.PluginReloadService>() is { } reload)
            {
                return RunPluginReloadAsync(reload, ctx.Writer);
            }

            ctx.Writer("Plugins: not available in this build (HARBOR_MINIMAL).");
            return Task.FromResult(Result.Success());
        });

        Register("tui", [], ["ansi", "plain", "spectre", "consoleex", "notifications"], (ctx, _) =>
        {
            ctx.Writer("TUI: ansi (default), plain, spectre, fullscreen");
            return Task.FromResult(Result.Success());
        });

        Register("storage", [], ["jsonl", "memory", "sqlite"], (ctx, _) =>
        {
            ctx.Writer("Storage: jsonl (default), memory, sqlite");
            return Task.FromResult(Result.Success());
        });

        Register("renderer", [], null, (ctx, _) =>
        {
            if (ctx.Services.GetService<Harbor.Hosting.Rendering.IRendererPipeline>() is not { } pipeline)
            {
                ctx.Writer("Renderer pipeline: not available in this build.");
                return Task.FromResult(Result.Success());
            }

            ctx.Writer($"Renderer: {pipeline.CurrentBackendId} | available: {string.Join(", ", pipeline.AvailableBackends)}");
            ctx.Writer("Usage: /renderer <backend>");
            return Task.FromResult(Result.Success());
        });

        return dict.ToFrozenDictionary();
    }

    private static ICommandContext MakeCtx(CommandContext ctx) =>
        new SimpleCommandContext(ctx.Session, ctx.Agent, ctx.Providers, ctx.ToolRegistry, ctx.Writer, ctx.Reader!);

    private static async Task<Result> RunPluginReloadAsync(
        Harbor.Hosting.PluginReloadService reload, Action<string> writer)
    {
        var summary = await reload.ReloadAsync().ConfigureAwait(false);
        writer(summary.Loaded == 0
            ? "Plugins: no new plugin(s) loaded."
            : $"Plugins: {summary.Loaded} loaded.");
        foreach (var note in summary.Notes)
            writer($"  - {note}");
        writer("Hint: edited/removed plugins need a restart to fully rebind.");
        return Result.Success();
    }

    /// <summary>Minimal ISlashCommand adapter for palette consumption.</summary>
    private sealed record DelegateSlashCommand(
        string Name,
        IReadOnlyList<string> Aliases,
        IReadOnlyList<string>? ArgSuggestions) : ISlashCommand
    {
        public string Description => "";
        public string Usage => $"/{Name}";
        public Task<Result> ExecuteAsync(IReadOnlyList<string> args, ICommandContext context, CancellationToken ct = default)
            => Task.FromResult(Result.Failure("Delegate command — use SlashCommandDispatcher to execute."));
    }
}

/// <summary>
///     Static helper used by Program.cs for non-interactive CLI commands
///     (<c>harbor ask</c>, <c>harbor setup</c>, etc.).
/// </summary>
internal static class SlashCommandDispatcherStatic
{
    public static async Task<int?> TryHandleAsync(string commandName, string[] args, ICommand[] commands, CancellationToken ct = default)
    {
        var command = commands.FirstOrDefault(c => c.Name.Equals(commandName, StringComparison.OrdinalIgnoreCase));
        if (command is null)
        {
            return null;
        }

        return await command.ExecuteAsync(args, ct).ConfigureAwait(false);
    }
}
