// SlashCommandFailureSurfacesTests.cs — issue #603: a slash command that fails
// must TELL THE USER.
//
// THE DEFECT THIS PINS
// --------------------
// `SlashCommandDispatcher` collects handlers as
// `Func<CommandContext, IReadOnlyList<string>, Task<Result>>` and the sole
// consumer assigned the value to a local it never read:
//
//     var result = await reg.Execute(ctx, args);   // :192
//     return SlashCommandOutcome.Continue;          // :194
//
// `SlashCommandOutcome` is `(bool ShouldQuit, int ExitCode)` — no error member —
// and all three production entry points (`ReplRunner` x2, `PromptPipeline` via
// `LegacySlashRunner`) read only those two. So the `Result` could not carry a
// failure anywhere.
//
// The consequence is not stylistic. A `Result` that is dropped is a contract
// that PROMISES the failure was handled; the handlers that returned
// `Result.Failure` therefore looked correct while the user saw nothing. The
// sharpest case was `/sessions`: on a store it could not read it printed no
// line at all and returned `Result.Success()` after the store had already
// failed — silence, which reads as "you have no sessions" and sends the user
// off to start a new session believing their history is gone.
//
// WHAT IS ASSERTED HERE
// ---------------------
// The user-visible channel, per command, driven through the real dispatcher:
// an unreadable store / unwritable config / unsaveable permissions must put
// the reason on the writer. `/sessions` and `/auth list` are the two whose
// "printed nothing" was indistinguishable from a legitimately empty result,
// so they are the two that most needed a test.
//
// The structural half — that the dispatcher's read of the `Result` is real, not
// ceremonial — is `SkillsUpdate_Failure_ReachesTheDispatcherLog`: a handler
// returning `Result.Failure` now produces a Warning naming the command, so a
// handler that forgets to write is still diagnosable from `harbor logs`.
//
// The source-level gate that stops the shape coming back is
// tests/Harbor.Architecture.Tests/SlashResultChannelTests.cs.
//
// NOT ASSERTED, ON PURPOSE: the outcome carries no error, and adding one would
// be the wrong fix. Every caller wants the same two things (keep looping; what
// exit code), the user-facing report is the writer by design, and an error
// member that no caller reads would just relocate the dead channel one frame
// up. A failed command is a command error, not a crash — the REPL keeps running,
// which `Sessions_StoreFailure_KeepsTheReplRunning` pins.

using System.Text.Json;
using CSharpFunctionalExtensions;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Permissions;
using Harbor.Abstractions.Sessions;
using Harbor.Abstractions.Tui;
using Harbor.App.Cli.Commands;
using Harbor.App.Cli.Hosting;
using Harbor.App.Cli.Repl;
using Harbor.Application.Configuration;
using Harbor.Application.Onboarding;
using Harbor.Application.Permissions;
using Harbor.Application.Skills;
using Harbor.TestKit;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;

namespace Harbor.App.Cli.Tests;

/// <summary>
///     Issue #603: a slash-command failure must reach the user, and the
///     <c>Result</c> the dispatcher reads must be read for a reason.
/// </summary>
public class SlashCommandFailureSurfacesTests
{
    private const string StoreError = "disk gone";
    private const string ConfigError = "config file is read-only";
    private const string PermissionsError = "cannot write permissions.json";

    // ── /sessions — the issue's headline case ───────────────────────────────

    [Test]
    public async Task Sessions_StoreFailure_ReportsTheReasonToTheUser()
    {
        var lines = await DispatchAsync("/sessions", new FailingStore());

        await Assert.That(lines.Count).IsGreaterThan(0)
            .Because(
                "Before #603 this produced NO output at all: the handler branched on `IsSuccess`, had no "
                + "failure arm, and returned `Result.Success()`. Silence reads as \"you have no sessions\", "
                + "which is how a user ends up starting a new session believing their history is gone.");

        await Assert.That(lines[0]).Contains(StoreError)
            .Because("The reason the store gave is the reason the user needs — not a generic \"failed\".");
    }

    [Test]
    public async Task Sessions_StoreFailure_KeepsTheReplRunning()
    {
        var outcome = await DispatchOutcomeAsync("/sessions", new FailingStore());

        await Assert.That(outcome.ShouldQuit).IsFalse()
            .Because(
                "A command error is a command error, not a crash. `/tree` already behaves this way and the "
                + "contract on Register says so; a store that cannot be read must not end the session.");
    }

    [Test]
    public async Task Sessions_EmptyStore_ReportsNothingAndSucceeds()
    {
        var lines = await DispatchAsync("/sessions", new EmptyStore());

        await Assert.That(lines).IsEmpty()
            .Because(
                "The failure fix must not turn a genuinely empty store into a complaint. Silence is correct "
                + "here and only here — which is exactly why the failure case needed a test to tell the two "
                + "apart.");
    }

    [Test]
    public async Task Sessions_ListsSessionsWhenTheStoreWorks()
    {
        var store = new SeededStore();
        store.Add(Session.Create("/harbor-603", "code", "t", "m") with { Id = "s1", Title = "kept" });

        var lines = await DispatchAsync("/sessions", store);

        await Assert.That(lines.Count).IsEqualTo(1);
        await Assert.That(lines[0]).Contains("s1");
        await Assert.That(lines[0]).Contains("kept");
    }

    // ── parity with the two handlers that were already right ────────────────

    [Test]
    public async Task Tree_StoreFailure_WordsItTheSameWayAsSessions()
    {
        var lines = await DispatchAsync("/tree", new FailingStore());

        await Assert.That(lines.Count).IsGreaterThan(0);
        await Assert.That(lines[0]).Contains(StoreError);
        await Assert.That(lines[0].StartsWith("Cannot list sessions:")).IsTrue()
            .Because(
                "The same store fails for both commands, so both must word it identically — otherwise the "
                + "user sees two different-looking failures for one broken disk, and the copy has to be "
                + "maintained in two places. `/tree` already had this arm and is the reference.");
    }

    // ── the delegated commands ───────────────────────────────────────────────
    //
    // These three are tested through the command classes directly rather than
    // through the dispatcher, because that is the level the fix lives at: the
    // arm that was added is inside AuthCommand/ConfigCommand/PermissionsCommand.
    //
    // A dispatch-level test would also be misleading today, and the reason is
    // worth recording: the five delegated registrations in SlashCommandDispatcher
    // bind `(ctx, _)` and then call `ExecuteAsync(Array.Empty<string>(), ...)`,
    // so `/auth list`, `/config set …` and `/permissions clear` never receive
    // their arguments and always take the no-args branch. That is a separate,
    // pre-existing defect (it makes every argument-taking slash command a no-op)
    // and fixing it is out of scope here — but it is why the tests below call
    // the commands directly.

    [Test]
    public async Task AuthList_ConfigFailure_ReportsTheReasonToTheUser()
    {
        var lines = new List<string>();
        var auth = new AuthCommand(new AuthStore(new FailingConfigStore()), lines.Add);

        var result = await auth.ExecuteAsync(["list"], MakeContext(lines));

        await Assert.That(lines.Any(l => l.Contains(ConfigError))).IsTrue()
            .Because(
                "`/auth list` branched on `IsSuccess` with no failure arm and returned Success(), so an "
                + "unreadable config printed nothing — identical to \"you have no API keys set\". The same "
                + "method backs `harbor auth list`, which maps the Result to an exit code, so this was a "
                + "wrong-answer-to-a-successful-exit-code too.");

        await Assert.That(result.IsFailure).IsTrue()
            .Because(
                "The verb path consumes this Result, so it must stay a failure — reporting it to the user "
                + "must not turn the exit code into a success.");
    }

    [Test]
    public async Task ConfigSet_SaveFailure_ReportsTheReasonToTheUser()
    {
        // The store LOADS fine and only fails to write: ConfigCommand loads the
        // config first and returns early on a load failure, so a store that
        // failed to load would pass this test through a different branch and
        // prove nothing about the arm that was added.
        var lines = new List<string>();
        var config = new ConfigCommand(new UnwritableConfigStore(), lines.Add);

        var result = await config.ExecuteAsync(["set", "model", "gpt-4"], MakeContext(lines));

        await Assert.That(lines.Any(l => l.Contains(ConfigError))).IsTrue()
            .Because(
                "The success arm was the only one, so `/config set` against an unwritable config file "
                + "produced no output and a bare `return updateResult`. The REPL dropped that Result and "
                + "`ConfigVerb` sees only the exit code — the user was never told why the value did not change.");

        await Assert.That(result.IsFailure).IsTrue()
            .Because("ConfigVerb turns this Result into `harbor config set`'s exit code; it must stay a failure.");
    }

    [Test]
    public async Task ConfigSet_SaveSucceeds_StillReportsTheValue()
    {
        var lines = new List<string>();
        var config = new ConfigCommand(new WorkingConfigStore(), lines.Add);

        _ = await config.ExecuteAsync(["set", "model", "gpt-4"], MakeContext(lines));

        await Assert.That(lines.Any(l => l.Contains("gpt-4"))).IsTrue()
            .Because("The new failure arm must not change the success path — the old code printed this line too.");
    }

    [Test]
    public async Task PermissionsClear_SaveFailure_ReportsTheReasonToTheUser()
    {
        var lines = new List<string>();
        var permissions = new PermissionsCommand(
            new FailingPermissionService(), new FakeAgentRegistry(), new JsonConfigStore(), lines.Add);

        var result = await permissions.ExecuteAsync(["clear"], MakeContext(lines));

        await Assert.That(lines.Any(l => l.Contains(PermissionsError))).IsTrue()
            .Because(
                "`ClearRules` treated `return saveResult` as its failure handling, but the slash dispatcher "
                + "has no use for a Result. So `/permissions clear` against an unsaveable store printed "
                + "nothing and the user kept their rules with no indication. `SetRule` already had this arm.");

        await Assert.That(result.IsFailure).IsTrue()
            .Because("The user must see it AND the caller must still be able to tell it failed.");
    }

    [Test]
    public async Task PermissionsClear_SaveSucceeds_StillReportsTheClear()
    {
        var lines = new List<string>();
        var permissions = new PermissionsCommand(
            new WorkingPermissionService(), new FakeAgentRegistry(), new JsonConfigStore(), lines.Add);

        _ = await permissions.ExecuteAsync(["clear"], MakeContext(lines));

        await Assert.That(lines.Any(l => l.Contains("Cleared all persisted permission rules"))).IsTrue()
            .Because(
                "The new failure arm in ClearRules must not swallow the success path. A fake service is used "
                + "rather than the real PermissionService so the test never writes the developer's own "
                + "config file.");
    }

    // ── /setup — the wizard's Result was dropped here and nowhere else ───────

    [Test]
    public async Task Setup_WizardAborts_ExplainsWhy()
    {
        // Three empty answers is exactly how OnboardingWizard gives up
        // ("Non-interactive input or setup aborted."), so the wizard fails for
        // real without a fake. Its Result is consumed on the boot path
        // (ReplRunner.cs:150); on the slash path it used to be returned into the
        // dispatcher and dropped, leaving the prompts with no ending.
        var lines = await DispatchAsync("/setup", new EmptyStore());

        await Assert.That(lines.Any(l => l.Contains("Setup failed"))).IsTrue()
            .Because(
                "The wizard writes its prompts but nothing on the way out, so `/setup` followed by three "
                + "blank lines (or Ctrl-D on a piped stdin) used to end with no explanation whatsoever.");

        await Assert.That(lines.Any(l => l.Contains("aborted"))).IsTrue()
            .Because("A bare \"Setup failed\" with no reason is half the fix; the wizard's own message is the reason.");
    }

    // ── the structural half: the dispatcher's read of the Result is real ────

    [Test]
    public async Task SkillsUpdate_Failure_ReachesTheDispatcherLog()
    {
        var log = new CapturingLogger<SlashCommandDispatcher>();
        var lines = await DispatchAsync(
            "/skills update",
            new EmptyStore(),
            logger: log,
            skillUpdate: _ => Task.FromResult(SkillUpdateReport.Failed("could not read Username")));

        // The user-facing half (pre-existing, and correct on its own).
        await Assert.That(lines.Any(l => l.Contains("could not read Username"))).IsTrue();

        // The channel half: before #603 the Result was assigned at :192 and
        // nothing read it, so a handler that returned Failure without writing
        // left no trace anywhere — not on screen, not in `harbor logs`.
        await Assert.That(log.Entries.Any(e =>
                e.Level == LogLevel.Warning
                && e.Message.Contains("skills", StringComparison.OrdinalIgnoreCase)
                && e.Message.Contains("could not read Username", StringComparison.Ordinal)))
            .IsTrue()
            .Because(
                "This is the assertion that the Result channel has a consumer. The dispatcher cannot know "
                + "whether a handler remembered to write, so it logs the failure itself; a swallowed "
                + "command failure is then diagnosable from `harbor logs` instead of vanishing.");
    }

    [Test]
    public async Task Help_Success_IsNotLoggedAsAWarning()
    {
        var log = new CapturingLogger<SlashCommandDispatcher>();
        _ = await DispatchAsync("/help", new EmptyStore(), logger: log);

        await Assert.That(log.Entries.Any(e => e.Level == LogLevel.Warning)).IsFalse()
            .Because(
                "The Warning is for failures only. `/help` succeeds, and a log entry for it would be the "
                + "noise that trains people to ignore the entry that matters.");
    }

    // ── harness ─────────────────────────────────────────────────────────────

    /// <summary>
    ///     The context the delegated slash commands receive. Built the same way
    ///     <c>SlashCommandDispatcher.MakeCtx</c> builds it, so a command that
    ///     starts reading <c>context.Session.Session.Agent</c> behaves as it
    ///     would in the REPL.
    /// </summary>
    private static ICommandContext MakeContext(List<string> lines) =>
        new SimpleCommandContext(
            Session.Create("/harbor-603", "code", "t", "m"),
            null!,
            null!,
            new FakeToolRegistry(),
            lines.Add,
            _ => Task.FromResult(string.Empty));

    private static async Task<List<string>> DispatchAsync(
        string input,
        ISessionStore store,
        CapturingLogger<SlashCommandDispatcher>? logger = null,
        Func<IReadOnlyList<string>, Task<SkillUpdateReport>>? skillUpdate = null)
    {
        var lines = new List<string>();
        var config = new JsonConfigStore();

        // The cast is required: `??` needs a common type, and CapturingLogger<T>
        // and NullLogger<T> are siblings, not a base/derived pair.
        ILogger<SlashCommandDispatcher> log = logger
                                             ?? (ILogger<SlashCommandDispatcher>)NullLogger<SlashCommandDispatcher>.Instance;

        var dispatcher = new SlashCommandDispatcher(
            log,
            new FakeToolRegistry(),
            store,
            new OnboardingWizard(config, new AuthStore(config)),
            new PermissionService(new FakeAgentRegistry(), NullLogger<PermissionService>.Instance),
            skillUpdate: skillUpdate);

        var outcome = await dispatcher.HandleCoreAsync(input,
            writer: lines.Add,
            reader: _ => Task.FromResult(string.Empty),
            agent: null!, agentRegistry: null!, configStore: config, authStore: new AuthStore(config),
            providers: null!, session: Session.Create("/harbor-603", "code", "t", "m"));

        await Assert.That(outcome.ShouldQuit).IsFalse();
        return lines;
    }

    private static async Task<SlashCommandOutcome> DispatchOutcomeAsync(string input, ISessionStore store)
    {
        var config = new JsonConfigStore();
        var dispatcher = new SlashCommandDispatcher(
            NullLogger<SlashCommandDispatcher>.Instance,
            new FakeToolRegistry(),
            store,
            new OnboardingWizard(config, new AuthStore(config)),
            new PermissionService(new FakeAgentRegistry(), NullLogger<PermissionService>.Instance));

        return await dispatcher.HandleCoreAsync(input,
            writer: _ => { },
            reader: _ => Task.FromResult(string.Empty),
            agent: null!, agentRegistry: null!, configStore: config, authStore: new AuthStore(config),
            providers: null!, session: Session.Create("/harbor-603", "code", "t", "m"));
    }

    // ── fakes ───────────────────────────────────────────────────────────────

    /// <summary>
    ///     Session store whose listing fails, with a recognisable reason. Only
    ///     <see cref="ListAsync" /> is overridden because it is the only member
    ///     <c>/sessions</c> and <c>/tree</c> reach for.
    /// </summary>
    private sealed class FailingStore : StoreBase
    {
        public override Task<Result<IReadOnlyList<Session>>> ListAsync(string? projectId = null, CancellationToken ct = default)
            => Task.FromResult(Result.Failure<IReadOnlyList<Session>>(StoreError));
    }

    /// <summary>Session store that works and holds nothing.</summary>
    private sealed class EmptyStore : StoreBase
    {
    }

    /// <summary>Session store that works and holds one session.</summary>
    private sealed class SeededStore : StoreBase
    {
        private readonly Dictionary<string, Session> _sessions = [];

        public void Add(Session session) => _sessions[session.Id] = session;

        public override Task<Result<IReadOnlyList<Session>>> ListAsync(string? projectId = null, CancellationToken ct = default)
            => Task.FromResult(Result.Success<IReadOnlyList<Session>>([.. _sessions.Values]));
    }

    /// <summary>
    ///     Members the failure tests do not exercise fail loudly rather than
    ///     returning an empty success, so a command that starts calling one shows
    ///     up as a test failure instead of as a mysteriously blank list.
    /// </summary>
    private abstract class StoreBase : ISessionStore
    {
        /// <summary>Succeeds with nothing — the "you have no sessions" baseline.</summary>
        public virtual Task<Result<IReadOnlyList<Session>>> ListAsync(string? projectId = null, CancellationToken ct = default)
            => Task.FromResult(Result.Success<IReadOnlyList<Session>>([]));

        public Task<Result<IReadOnlyList<AgentMessage>>> GetMessagesAsync(string sessionId, CancellationToken ct = default)
            => throw new NotSupportedException("Not exercised by the /sessions failure tests.");

        public Task<Result<SessionMetadata>> GetStatsAsync(string sessionId, CancellationToken ct = default)
            => throw new NotSupportedException("Not exercised by the /sessions failure tests.");

        public Task<Result<Session>> GetAsync(string sessionId, CancellationToken ct = default)
            => Task.FromResult(Result.Failure<Session>($"Session '{sessionId}' not found."));

        public Task<Result<Session>> CreateAsync(string directory, string agentName, string providerId, string modelId, CancellationToken ct = default)
            => throw new NotSupportedException("Not exercised by the /sessions failure tests.");

        public Task<Result> AppendMessageAsync(string sessionId, AgentMessage message, CancellationToken ct = default)
            => throw new NotSupportedException("Not exercised by the /sessions failure tests.");

        public Task<Result> UpdateAsync(Session session, CancellationToken ct = default)
            => throw new NotSupportedException("Not exercised by the /sessions failure tests.");

        public Task<Result> UpdateMessageAsync(string sessionId, AgentMessage message, CancellationToken ct = default)
            => throw new NotSupportedException("Not exercised by the /sessions failure tests.");

        public Task<Result<int>> DeleteMessagesAfterAsync(string sessionId, string messageId, CancellationToken ct = default)
            => throw new NotSupportedException("Not exercised by the /sessions failure tests.");

        public Task<Result> UpdateStatsAsync(string sessionId, SessionMetadata metadata, CancellationToken ct = default)
            => throw new NotSupportedException("Not exercised by the /sessions failure tests.");

        public Task<Result> DeleteAsync(string sessionId, CancellationToken ct = default)
            => throw new NotSupportedException("Not exercised by the /sessions failure tests.");
    }

    /// <summary>Config store whose every read and write fails.</summary>
    private sealed class FailingConfigStore : IConfigStore
    {
        public Task<Result<HarborConfig>> LoadAsync(CancellationToken ct = default)
            => Task.FromResult(Result.Failure<HarborConfig>(ConfigError));

        public Task<Result> SaveAsync(HarborConfig config, CancellationToken ct = default)
            => Task.FromResult(Result.Failure(ConfigError));

        public Task<Result> UpdateAsync(Func<HarborConfig, HarborConfig> updater, CancellationToken ct = default)
            => Task.FromResult(Result.Failure(ConfigError));

        public Task<Result<string>> GetApiKeyAsync(string providerId, CancellationToken ct = default)
            => Task.FromResult(Result.Failure<string>(ConfigError));
    }

    /// <summary>Config store that reads fine and cannot be written — the shape of a
    ///     read-only or root-owned <c>config.json</c>. Loads succeed on purpose,
    ///     because <c>ConfigCommand</c> loads before it writes and returns early
    ///     on a load failure.
    /// </summary>
    private sealed class UnwritableConfigStore : IConfigStore
    {
        public Task<Result<HarborConfig>> LoadAsync(CancellationToken ct = default)
            => Task.FromResult(Result.Success(HarborConfig.Default));

        public Task<Result> SaveAsync(HarborConfig config, CancellationToken ct = default)
            => Task.FromResult(Result.Failure(ConfigError));

        public Task<Result> UpdateAsync(Func<HarborConfig, HarborConfig> updater, CancellationToken ct = default)
            => Task.FromResult(Result.Failure(ConfigError));

        public Task<Result<string>> GetApiKeyAsync(string providerId, CancellationToken ct = default)
            => Task.FromResult(Result.Failure<string>(ConfigError));
    }

    /// <summary>
    ///     Config store that works entirely in memory, so the success-path test
    ///     never writes the developer's own <c>config.json</c>.
    /// </summary>
    private sealed class WorkingConfigStore : IConfigStore
    {
        public HarborConfig Current { get; set; } = HarborConfig.Default;

        public Task<Result<HarborConfig>> LoadAsync(CancellationToken ct = default)
            => Task.FromResult(Result.Success(Current));

        public Task<Result> SaveAsync(HarborConfig config, CancellationToken ct = default)
        {
            Current = config;
            return Task.FromResult(Result.Success());
        }

        public Task<Result> UpdateAsync(Func<HarborConfig, HarborConfig> updater, CancellationToken ct = default)
        {
            Current = updater(Current);
            return Task.FromResult(Result.Success());
        }

        public Task<Result<string>> GetApiKeyAsync(string providerId, CancellationToken ct = default)
            => Task.FromResult(Result.Failure<string>("not set"));
    }

    /// <summary>Permission service whose persist always fails.</summary>
    private sealed class FailingPermissionService : IPermissionService
    {
        public Task<Result> SaveAsync(CancellationToken ct = default)
            => Task.FromResult(Result.Failure(PermissionsError));

        public PermissionRuleset GetRuleset(string agentName) => PermissionRuleset.Empty;

        public Task<Result<PermissionResponse>> CheckAsync(
            string agentName, string toolName, JsonElement args,
            CancellationToken ct = default, string? invocationId = null, int generation = 1)
            => throw new NotSupportedException("Not exercised by the /permissions failure tests.");

        public Task<Result<PermissionResponse>> AskUserAsync(PermissionRequest request, CancellationToken ct = default)
            => throw new NotSupportedException("Not exercised by the /permissions failure tests.");
    }

    /// <summary>
    ///     Permission service that persists in memory, so the success-path test
    ///     never writes the developer's real config file.
    /// </summary>
    private sealed class WorkingPermissionService : IPermissionService
    {
        public Task<Result> SaveAsync(CancellationToken ct = default)
            => Task.FromResult(Result.Success());

        public PermissionRuleset GetRuleset(string agentName) => PermissionRuleset.Empty;

        public Task<Result<PermissionResponse>> CheckAsync(
            string agentName, string toolName, JsonElement args,
            CancellationToken ct = default, string? invocationId = null, int generation = 1)
            => throw new NotSupportedException("Not exercised by the /permissions failure tests.");

        public Task<Result<PermissionResponse>> AskUserAsync(PermissionRequest request, CancellationToken ct = default)
            => throw new NotSupportedException("Not exercised by the /permissions failure tests.");
    }

    /// <summary>Captures log entries so a test can assert on them.</summary>
    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, formatter(state, exception)));
    }
}
