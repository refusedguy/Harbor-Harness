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
using Harbor.Application.Skills;
using Harbor.Terminal.Abstractions;
using Harbor.Ui.Framework.Commands;
using Harbor.Ui.Framework.Projection;
using Microsoft.Extensions.Logging;
using Harbor.Registries.Agents;
using Harbor.Registries.Tools;

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
    private readonly IToolRegistry _tools;
    private readonly ISessionStore _sessions;
    private readonly OnboardingWizard _wizard;
    private readonly IPermissionService _permissions;

    /// <summary>
    ///     Optional host services (#63 legitimate: a MINIMAL host never
    ///     registers the plugin runtime or the renderer pipeline, so their
    ///     absence is a graceful fallback, never a missing-dependency error).
    /// </summary>
    private readonly Harbor.Hosting.PluginReloadService? _pluginReload;
    private readonly Harbor.Hosting.Rendering.IRendererPipeline? _rendererPipeline;

    /// <summary>
    ///     Refresh delegate for <c>/skills refresh</c> (issue #23 slice 2):
    ///     reseeds the shared <see cref="SkillFreshnessModel" /> from the
    ///     workspace and returns the new snapshot. Null on hosts without a
    ///     model — the handler reports "not available" instead of failing.
    /// </summary>
    private readonly Func<IReadOnlyList<SkillFreshnessEntry>>? _skillRefresh;

    /// <summary>
    ///     Update delegate for <c>/skills update</c> (issue #384): re-resolves
    ///     the named skills (or every stale one) from their git source and
    ///     reseeds the shared model on success. Null on hosts without a model —
    ///     the handler reports "not available" instead of failing.
    /// </summary>
    private readonly Func<IReadOnlyList<string>, Task<SkillUpdateReport>>? _skillUpdate;

    /// <summary>All registered slash commands (canonical + aliases → single registration).</summary>
    private sealed record SlashCommandRegistration(
        SlashCommandDefinition Definition,
        Func<CommandContext, IReadOnlyList<string>, Task<Result>> Execute);

    /// <summary>
    ///     The per-call state a command is handed.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         #483 — this was a sixteen-member bag handed to all seventeen
    ///         handlers, and eight of those members were not per-call state at all:
    ///         they are this dispatcher's OWN lifetime fields, assigned before
    ///         <c>BuildRegistry()</c> runs. Re-boxing them here handed every command
    ///         reach it never asked for — <c>/help</c> could name
    ///         <c>PluginReloadService</c> — and it is why the three host-shape
    ///         optionals (absent on a MINIMAL host) leaked into the parameter every
    ///         command receives.
    ///     </para>
    ///     <para>
    ///         Each <c>Register*Commands</c> group now closes over the field it
    ///         needs instead, so the compiler forbids a command from naming a
    ///         service it did not ask for. What remains is genuinely per-call: it
    ///         arrives as a <see cref="HandleCoreAsync" /> argument and changes
    ///         between dispatches. Narrowing further (four of seventeen commands
    ///         need only <c>Writer</c>) needs per-command handler types rather than
    ///         one shared bag.
    ///     </para>
    ///     <para>
    ///         Guarded by <c>tests/Harbor.Architecture.Tests/SlashCommandContextScopeTests.cs</c>.
    ///     </para>
    /// </remarks>
    public sealed record CommandContext(
        Action<string> Writer,
        Func<string, Task<string>>? Reader,
        Session Session,
        IAgent Agent,
        IAgentRegistry AgentRegistry,
        IProviderRegistry Providers,
        IConfigStore ConfigStore,
        AuthStore AuthStore);

    public SlashCommandDispatcher(
        ILogger<SlashCommandDispatcher> logger,
        IToolRegistry tools,
        ISessionStore sessions,
        OnboardingWizard wizard,
        IPermissionService permissions,
        Harbor.Hosting.PluginReloadService? pluginReload = null,
        Harbor.Hosting.Rendering.IRendererPipeline? rendererPipeline = null,
        Func<IReadOnlyList<SkillFreshnessEntry>>? skillRefresh = null,
        Func<IReadOnlyList<string>, Task<SkillUpdateReport>>? skillUpdate = null)
    {
        _logger = logger;
        _tools = tools;
        _sessions = sessions;
        _wizard = wizard;
        _permissions = permissions;
        _pluginReload = pluginReload;
        _rendererPipeline = rendererPipeline;
        _skillRefresh = skillRefresh;
        _skillUpdate = skillUpdate;
        _byName = BuildRegistry();
    }

    public async Task<SlashCommandOutcome> HandleAsync(
        string input, ITuiRenderer renderer,
        IAgent agent, IAgentRegistry agentRegistry,
        IConfigStore configStore, AuthStore authStore,
        IProviderRegistry providers, Session session)
    {
        return await HandleCoreAsync(input,
            writer: msg => _ = renderer.WriteLineAsync(msg),
            reader: async prompt =>
            {
                var r = await renderer.ReadLineAsync(prompt).ConfigureAwait(false);
                return r.GetValueOrDefault(string.Empty);
            },
            agent, agentRegistry, configStore, authStore, providers, session).ConfigureAwait(false);
    }

    /// <summary>
    ///     CE-4: renderer-free overload for the CellForge REPL — output goes to
    ///     the chat timeline and input comes from the composer instead of an
    ///     <see cref="ITuiRenderer" />.
    /// </summary>
    public Task<SlashCommandOutcome> HandleCoreAsync(
        string input,
        Action<string> writer, Func<string, Task<string>> reader,
        IAgent agent, IAgentRegistry agentRegistry,
        IConfigStore configStore, AuthStore authStore,
        IProviderRegistry providers, Session session)
    {
        string[] parts = input[1..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return Task.FromResult(SlashCommandOutcome.Continue);

        string cmd = parts[0].ToLowerInvariant();
        string[] args = parts.Skip(1).ToArray();

        if (SlashCommandCatalog.Find(cmd) is { QuitsLoop: true })
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

        // #483: only per-call state travels here. Everything the dispatcher owns
        // for its own lifetime was closed over by the registration groups, so it
        // is not re-boxed per dispatch and not in reach of a command.
        var ctx = new CommandContext(writer, reader, session, agent, agentRegistry, providers,
            configStore, authStore);

        return ExecuteRegisteredAsync(reg, ctx, args);
    }

    /// <summary>All registered slash commands, including aliases.</summary>
    public IReadOnlyList<ISlashCommand> GetRegisteredCommands()
    {
        var list = new List<ISlashCommand>(SlashCommandCatalog.All.Count);
        foreach (SlashCommandDefinition def in SlashCommandCatalog.All)
        {
            list.Add(new CatalogSlashCommand(def));
        }

        return list;
    }

    /// <summary>
    ///     Arg suggestions for the named command, or <see langword="null" /> when
    ///     the command takes no arguments or suggestions are not applicable.
    /// </summary>
    public IReadOnlyList<string>? GetArgSuggestions(string commandName)
    {
        return SlashCommandCatalog.Find(commandName)?.ArgSuggestions;
    }

    private async Task<SlashCommandOutcome> ExecuteRegisteredAsync(
        SlashCommandRegistration reg, CommandContext ctx, IReadOnlyList<string> args)
    {
        try
        {
            _logger.LogInformation("Slash command: /{Command} args={ArgCount}", reg.Definition.Name, args.Count);

            // #603: the handler's Result used to be assigned here and never read.
            // `SlashCommandOutcome` carries only ShouldQuit/ExitCode and all three
            // production entry points read only those, so the channel was
            // decorative: `/sessions` could swallow a failed ListAsync, return
            // Success(), and nothing downstream would ever know. The user-facing
            // report is the handler's own `ctx.Writer` call (see the contract on
            // Register); reading the value here is what makes the failure
            // diagnosable when a handler forgets, and it is the guard
            // tests/Harbor.Architecture.Tests/SlashResultChannelTests.cs enforces.
            var result = await reg.Execute(ctx, args).ConfigureAwait(false);
            if (result.IsFailure)
            {
                // A command error is not a crash: the REPL keeps running. Log it
                // at Warning so `harbor logs` still shows what the user saw (or,
                // for a handler that forgot to write, did not see).
                _logger.LogWarning("Command /{Command} failed: {Error}", reg.Definition.Name, result.Error);
            }
            else
            {
                _logger.LogDebug("Command /{Command} completed", reg.Definition.Name);
            }

            return SlashCommandOutcome.Continue;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error dispatching command /{Command}", reg.Definition.Name);
            ctx.Writer($"Error: {ex.Message}");
            return SlashCommandOutcome.Continue;
        }
    }

    // #483: an INSTANCE method, so each group can close over the lifetime fields
    // it needs. The constructor assigns every one of them before calling this, so
    // the closures capture initialised values — the ordering guarantee is at
    // `SlashCommandDispatcher(...)`, where `_byName = BuildRegistry()` is last.
    private FrozenDictionary<string, SlashCommandRegistration> BuildRegistry()
    {
        var dict = new Dictionary<string, SlashCommandRegistration>(capacity: 32);

        RegisterCoreCommands(dict);
        RegisterSessionCommands(dict);
        RegisterHostCommands(dict);
        RegisterSkillCommands(dict);

        // #462: the catalog is the single source of truth, so a handler that
        // drifted out of it (or a catalog entry nobody bound) fails loudly at
        // composition time instead of shipping a command the palette offers
        // but the dispatcher cannot run.
        AssertCatalogMatchesHandlers(dict);

        return dict.ToFrozenDictionary();
    }

    /// <summary>
    ///     Verifies the bound handlers and <see cref="SlashCommandCatalog" /> describe the
    ///     same command set. Loop-quitting commands (<c>/exit</c>, <c>/quit</c>) are handled
    ///     by <see cref="HandleCoreAsync" /> before the registry lookup, so they carry no
    ///     delegate and are excluded from the comparison.
    /// </summary>
    private static void AssertCatalogMatchesHandlers(Dictionary<string, SlashCommandRegistration> dict)
    {
        var missing = new List<string>();
        foreach (SlashCommandDefinition def in SlashCommandCatalog.All)
        {
            if (!def.QuitsLoop && !dict.ContainsKey(def.Name))
            {
                missing.Add(def.Name);
            }
        }

        if (missing.Count > 0)
        {
            throw new InvalidOperationException(
                $"SlashCommandCatalog declares commands with no handler: {string.Join(", ", missing)}.");
        }
    }

    /// <summary>
    ///     Runs a registered slash command and reports the outcome to the REPL.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         #603 — the handler contract. Returning <see cref="Result" /> does
    ///         <b>not</b> stop the REPL: a failed command is a command error, not
    ///         a crash, and <see cref="SlashCommandOutcome" /> has no error member
    ///         because no caller has ever had a use for one.
    ///     </para>
    ///     <para>
    ///         So the <see cref="Result" /> is a <b>declaration</b> that the
    ///         handler has handled the failure, not a transport that will deliver
    ///         it. The obligation it carries is: <i>if you return
    ///         <c>Result.Failure</c>, the reason has already been written through
    ///         <c>ctx.Writer</c></i>. <see cref="ExecuteRegisteredAsync" /> reads
    ///         the value only to log it, and
    ///         <c>tests/Harbor.Architecture.Tests/SlashResultChannelTests.cs</c>
    ///         fails the build when a handler branches on <c>IsSuccess</c> with no
    ///         failure arm, or drops an awaited <c>Result</c> entirely.
    ///     </para>
    ///     <para>
    ///         <c>/tree</c> and <c>/fork</c> are the reference shape: check
    ///         <c>IsFailure</c>, write the reason, return <c>Success()</c>.
    ///     </para>
    /// </remarks>
    private static void Register(
        Dictionary<string, SlashCommandRegistration> dict,
        string canonical,
        Func<CommandContext, IReadOnlyList<string>, Task<Result>> execute)
    {
        // Aliases, argument suggestions and descriptions live in the catalog
        // (#462). Passing them here as well is what let the advertised lists
        // drift away from what the dispatcher can actually run. The name must be
        // the canonical one — an alias here would silently bind the wrong command.
        if (SlashCommandCatalog.Find(canonical) is not { } def || def.Name != canonical)
        {
            throw new InvalidOperationException(
                $"/{canonical} is not a canonical SlashCommandCatalog entry.");
        }

        var reg = new SlashCommandRegistration(def, execute);
        dict[def.Name] = reg;
        foreach (string alias in def.Aliases)
        {
            dict[alias] = reg;
        }
    }

    // #483: an instance method closing over `_wizard`, `_tools` and `_permissions`.
    // Only genuinely per-call collaborators are read off `ctx` now.
    private void RegisterCoreCommands(Dictionary<string, SlashCommandRegistration> dict)
    {
        Register(dict, "help", (ctx, _) =>
        {
            // #462: derived from the catalog so /help can never advertise a
            // command the dispatcher cannot run (or omit one it can). Note this
            // drops /attach: it is a CellForge-local ReplCommandCatalog command
            // (like /vim and /panels), resolved by the composer submit path
            // rather than by this dispatcher, so it is not slash-dispatch
            // vocabulary and is not advertised to hosts that cannot run it.
            ctx.Writer("Commands: " + string.Join(" ", SlashCommandCatalog.Invocations));
            return Task.FromResult(Result.Success());
        });

        Register(dict, "new", (ctx, _) =>
        {
            ctx.Writer("Use /new in the interactive TUI to start a fresh session.");
            return Task.FromResult(Result.Success());
        });

        Register(dict, "setup", async (ctx, _) =>
        {
            var result = await _wizard
                .RunAsync(ctx.Reader!, ctx.Writer).ConfigureAwait(false);
            if (result.IsFailure)
            {
                // #603: the wizard writes prompts but nothing on the way out, and
                // its Result went straight back to this dispatcher, which dropped
                // it — so "/setup" then Ctrl-D or three blank lines ended with no
                // explanation at all. ReplRunner.cs:150 reports the same failure
                // on the boot-time path; this is the slash path's equivalent.
                ctx.Writer($"Setup failed: {result.Error}");
            }

            return result;
        });

        // #650: the five delegating registrations below used to bind `(ctx, _)`
        // and call `ExecuteAsync(Array.Empty<string>(), …)`, throwing the
        // arguments away at the call site. `HandleCoreAsync` had parsed them
        // correctly all along — the loss was here, one frame from the parse.
        // Every argument-taking slash command was therefore a no-op: `/config
        // set model gpt-4` took the no-args branch and printed the config dump
        // with `Model:` still showing the OLD value, which reads as "the switch
        // you asked for did not happen" right after a command that clearly ran.
        // Forwarding the delegate's own parameter is the whole fix; the guard
        // that keeps it fixed is rule C in
        // tests/Harbor.Architecture.Tests/SlashResultChannelTests.cs.

        Register(dict, "auth", (ctx, args) =>
        {
            return new AuthCommand(ctx.AuthStore, ctx.Writer)
                .ExecuteAsync(args, MakeCtx(ctx));
        });

        Register(dict, "model", (ctx, args) =>
        {
            return new ModelCommand(ctx.ConfigStore, ctx.Providers, ctx.Writer, ctx.Agent, ctx.Session)
                .ExecuteAsync(args, MakeCtx(ctx));
        });

        Register(dict, "agent", (ctx, args) =>
        {
            return new AgentCommand(ctx.ConfigStore, ctx.AgentRegistry, ctx.Writer)
                .ExecuteAsync(args, MakeCtx(ctx));
        });

        Register(dict, "config", (ctx, args) =>
        {
            return new ConfigCommand(ctx.ConfigStore, ctx.Writer)
                .ExecuteAsync(args, MakeCtx(ctx));
        });

        Register(dict, "permissions", (ctx, args) =>
        {
            return new PermissionsCommand(
                    _permissions,
                    ctx.AgentRegistry,
                    ctx.ConfigStore, ctx.Writer, ctx.Agent, ctx.Session)
                .ExecuteAsync(args, MakeCtx(ctx));
        });
    }

    // #483: an instance method closing over `_sessions` — the store is the
    // dispatcher's, not per-call state, so `/sessions` and `/tree` reach it
    // through the closure and no other command can.
    private void RegisterSessionCommands(Dictionary<string, SlashCommandRegistration> dict)
    {
        Register(dict, "providers", async (ctx, _) =>
        {
            var providers = ctx.Providers;
            ctx.Writer($"Providers ({providers.GetRegisteredProviderIds().Count}):");
            foreach (var id in providers.GetRegisteredProviderIds())
            {
                var r = providers.GetClient(id);
                ctx.Writer($"  [{(r.IsSuccess ? "OK" : "FAIL")}] {id}");
            }
            await Task.CompletedTask;
            return Result.Success();
        });

        Register(dict, "sessions", async (ctx, _) =>
        {
            var result = await _sessions.ListAsync().ConfigureAwait(false);
            if (result.IsFailure)
            {
                // #603: this had no failure arm and returned Success() anyway,
                // so an unreadable store printed nothing at all — which reads as
                // "you have no sessions". Same wording as /tree, which reports
                // the same store failing.
                ctx.Writer($"Cannot list sessions: {result.Error}");
                return Result.Success();
            }

            foreach (var s in result.Value)
                ctx.Writer($"  {s.Id} — {s.Title} [{s.ProviderId}/{s.Model}]");
            return Result.Success();
        });

        Register(dict, "tree", async (ctx, _) =>
        {
            var built = await SessionTreeRunner.BuildAsync(_sessions, ctx.Session.Id).ConfigureAwait(false);
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

        Register(dict, "fork", async (ctx, args) =>
        {
            if (args.Count < 2)
            {
                ctx.Writer("Usage: /fork <session-id> <message-id>");
                return Result.Success();
            }

            var outcome = await new SessionForkRunner(_sessions)
                .ForkAsync(args[0], args[1]).ConfigureAwait(false);
            if (outcome.IsFailure)
            {
                ctx.Writer($"Fork failed: {outcome.Error}");
                return Result.Success();
            }

            ctx.Writer($"Forked → {outcome.Value.ForkId}: copied {outcome.Value.Copied} message(s).");
            return Result.Success();
        });
    }

    // #483: an instance method closing over `_pluginReload` and `_rendererPipeline`.
    // Those two are nullable because a MINIMAL host never registers them — and
    // that host-shape fact now lives HERE, in the one group that can observe it,
    // instead of in the parameter handed to all seventeen commands.
    private void RegisterHostCommands(Dictionary<string, SlashCommandRegistration> dict)
    {
        Register(dict, "plugins", (ctx, _) =>
        {
            // Optional host service (absent on MINIMAL — see the field note).
            if (_pluginReload is { } reload)
            {
                return RunPluginReloadAsync(reload, ctx.Writer);
            }

            ctx.Writer("Plugins: not available in this build (HARBOR_MINIMAL).");
            return Task.FromResult(Result.Success());
        });

        Register(dict, "tui", (ctx, _) =>
        {
            ctx.Writer("TUI: ansi (default), plain, spectre, fullscreen");
            return Task.FromResult(Result.Success());
        });

        Register(dict, "storage", (ctx, _) =>
        {
            ctx.Writer("Storage: jsonl (default), memory, sqlite");
            return Task.FromResult(Result.Success());
        });

        Register(dict, "renderer", (ctx, _) =>
        {
            // Optional host service (absent on headless builds — see the field note).
            if (_rendererPipeline is not { } pipeline)
            {
                ctx.Writer("Renderer pipeline: not available in this build.");
                return Task.FromResult(Result.Success());
            }

            ctx.Writer($"Renderer: {pipeline.CurrentBackendId} | available: {string.Join(", ", pipeline.AvailableBackends)}");
            ctx.Writer("Usage: /renderer <backend>");
            return Task.FromResult(Result.Success());
        });
    }

    // #483: an instance method closing over `_skillRefresh` and `_skillUpdate`.
    // Both are nullable on a host without a freshness model, and `/skills` is the
    // only command that ever observes that.
    private void RegisterSkillCommands(Dictionary<string, SlashCommandRegistration> dict)
    {
        // KILLER_FEATURES §2.7 Feature 10 (issue #23 slice 2, issue #384):
        // `refresh` reseeds the shared SkillFreshnessModel from
        // skills-lock.json; `update [name…]` re-resolves stale skills from
        // their git source and then reseeds. The detailed per-skill panel
        // stays host opt-in — the default-on signal is the status-line
        // aggregate pill fed from the same model.
        Register(dict, "skills", async (ctx, args) =>
        {
            if (args.Count == 0)
            {
                WriteSkillsUsage(ctx);
                return Result.Success();
            }

            string verb = args[0];
            if (verb.Equals("refresh", StringComparison.OrdinalIgnoreCase))
            {
                if (args.Count != 1)
                {
                    WriteSkillsUsage(ctx);
                    return Result.Success();
                }

                if (_skillRefresh is null)
                {
                    ctx.Writer("Skill freshness: not available in this build.");
                    return Result.Success();
                }

                WriteSkillsSummary(ctx, _skillRefresh());
                return Result.Success();
            }

            if (!verb.Equals("update", StringComparison.OrdinalIgnoreCase))
            {
                WriteSkillsUsage(ctx);
                return Result.Success();
            }

            if (_skillUpdate is null)
            {
                ctx.Writer("Skill freshness: not available in this build.");
                return Result.Success();
            }

            // No names ⇒ every stale skill (resolved against the model).
            var names = args.Count > 1 ? args.Skip(1).ToArray() : Array.Empty<string>();
            var report = await _skillUpdate(names).ConfigureAwait(false);
            ctx.Writer(report.Outcome == SkillUpdateOutcome.Failed
                ? $"Skills: update failed — {report.Message} (freshness unchanged; run /skills refresh to re-check)"
                : $"Skills: {report.Message}");

            if (report.Outcome == SkillUpdateOutcome.Updated && _skillRefresh is { } refresh)
            {
                WriteSkillsSummary(ctx, refresh());
            }

            // A failed update is a command error, not a crash: the REPL keeps
            // running and the pill keeps its previous (stale) state. The reason
            // reaches the user through the `ctx.Writer` line above — not through
            // this Result, which the dispatcher only logs (#603).
            return report.Outcome == SkillUpdateOutcome.Failed
                ? Result.Failure(report.Message)
                : Result.Success();
        });
    }

    /// <summary><c>/skills</c> usage lines (verb forms only — args are documented in the flow).</summary>
    private static void WriteSkillsUsage(CommandContext ctx)
    {
        ctx.Writer("Usage: /skills refresh — reseed skill freshness from skills-lock.json.");
        ctx.Writer("Usage: /skills update [name…] — re-resolve stale skills from their source (all stale when no names).");
    }

    /// <summary>One-line freshness summary; identical wording for refresh and update.</summary>
    private static void WriteSkillsSummary(CommandContext ctx, IReadOnlyList<SkillFreshnessEntry> entries)
    {
        int stale = 0;
        for (int i = 0; i < entries.Count; i++)
        {
            if (entries[i].IsStale)
            {
                stale++;
            }
        }

        ctx.Writer(stale == 0
            ? $"Skills: {entries.Count} up to date."
            : $"Skills: {entries.Count} checked, {stale} need attention.");
    }

    // #483: `_tools` is the dispatcher's own field, so it is closed over here
    // rather than carried in the bag. The five delegating commands are the only
    // callers, and they are the only commands that build an `ICommandContext`.
    private ICommandContext MakeCtx(CommandContext ctx) =>
        new SimpleCommandContext(ctx.Session, ctx.Agent, ctx.Providers, _tools, ctx.Writer, ctx.Reader!);

    private static async Task<Result> RunPluginReloadAsync(
        Harbor.Hosting.PluginReloadService reload, Action<string> writer)
    {
        var summary = await reload.ReloadAsync().ConfigureAwait(false);
        // #1055s3: reload registers nothing in-process (Loaded is always 0) —
        // plugins run in harbor-plugins-host; the notes say where.
        foreach (var note in summary.Notes)
            writer($"Plugin reload: {note}");
        writer("Hint: plugins run in harbor-plugins-host — restart it to pick up changed scripts.");
        return Result.Success();
    }

    /// <summary>Catalog-backed <see cref="ISlashCommand" /> adapter for palette consumption.</summary>
    private sealed record CatalogSlashCommand(SlashCommandDefinition Definition) : ISlashCommand
    {
        public string Name => Definition.Name;
        public string Description => Definition.Description;
        public string Usage => Definition.Invocation;
        public IReadOnlyList<string> Aliases => Definition.Aliases;
        public IReadOnlyList<string>? ArgSuggestions => Definition.ArgSuggestions;
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
        Maybe<ICommand> command = commands.TryFirst(c => c.Name.Equals(commandName, StringComparison.OrdinalIgnoreCase));
        if (command.HasNoValue)
        {
            return null;
        }

        return await command.Value.ExecuteAsync(args, ct).ConfigureAwait(false);
    }
}
