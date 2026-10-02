using CSharpFunctionalExtensions;
using Harbor.Abstractions.Agents;
using Harbor.Abstractions.Events;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Tools;
using Harbor.App.Cli.Repl;
using Harbor.Application.Configuration;
using Harbor.Application.Onboarding;
using Harbor.Application.Permissions;
using Harbor.Terminal.Abstractions;
using Harbor.Terminal.Abstractions.Renderers;
using Harbor.TestKit;
using Harbor.Ui.Framework.State;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Harbor.App.Cli.Tests;

/// <summary>
///     #684 — the line REPL matched exit words with C# pattern syntax
///     (<c>trimmed is "exit" or "quit" or ":q"</c>), which is an <em>ordinal</em>
///     comparison, while every other entry point asked
///     <see cref="ChatCommands.ExitWords" />, an <see cref="StringComparer.OrdinalIgnoreCase" />
///     set. Two copies of one domain concept, and they had drifted: <c>QUIT</c> quit
///     the TUI and did <b>not</b> quit the line REPL — the same word behaving
///     differently depending on which entry point you launched.
/// </summary>
/// <remarks>
///     <para>
///         Which of the two comparisons is correct is settled by
///         <see cref="ExitWords_AreMatched_OrdinalIgnoreCase" /> rather than by taste,
///         and the short version is: <b>case-insensitive, everywhere</b>. The canonical
///         command registry already is — <c>SlashCommandCatalog</c> builds its lookup
///         with <see cref="StringComparer.OrdinalIgnoreCase" />, so <c>/EXIT</c> has always
///         resolved to <c>/exit</c> (<c>PaletteCatalogTests</c> pins exactly that), and
///         autocomplete matches prefixes case-insensitively, so typing <c>EXI</c> in
///         <em>this</em> REPL already offers <c>/exit</c>. A check that rejects the word the
///         completion popup just offered is the inconsistency, not the uppercase letters.
///         Making both comparisons ordinal instead would be a behaviour regression in the
///         TUI and the desktop app, which honour <c>QUIT</c> today.
///     </para>
///     <para>
///         These tests drive the real loop through <c>RunLineReplAsync</c> rather than
///         asserting on a predicate, because the observable difference is what the loop
///         <em>did</em>: an exit word is consumed and the REPL leaves, while an
///         unrecognised word is forwarded to the agent as a prompt. Counting reads and
///         inspecting forwarded prompts distinguishes the two unambiguously, and it
///         keeps working if the check is later moved behind a helper.
///     </para>
/// </remarks>
public class LineReplExitWordTests
{
    /// <summary>
    ///     Repo-standard hang guard (see <c>LineReplEofTests</c>): if a regression makes the
    ///     loop spin instead of leaving, the drive fails here as a named test rather than
    ///     wedging the CI shard until the job timeout.
    /// </summary>
    private static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(5);

    /// <summary>
    ///     Every case a user can type for a declared exit word. The lowercase rows passed
    ///     before and after the fix; the uppercase and title-case rows are the defect —
    ///     they are what <c>QUIT</c> did in the TUI and did not do here.
    /// </summary>
    private static readonly string[] TypedExitWords =
    [
        "exit", "EXIT", "Exit",
        "quit", "QUIT", "Quit",
        ":q", ":Q",
    ];

    /// <summary>
    ///     The defect itself: an exit word typed in any case ends the REPL, and is never
    ///     passed on to the agent. Before the fix the uppercase and title-case rows
    ///     reached <c>PromptAsync</c> instead — the REPL stayed open and the user's
    ///     attempt to leave was billed to the model as a prompt.
    /// </summary>
    [Test]
    [Arguments("exit")]
    [Arguments("EXIT")]
    [Arguments("Exit")]
    [Arguments("quit")]
    [Arguments("QUIT")]
    [Arguments("Quit")]
    [Arguments(":q")]
    [Arguments(":Q")]
    public async Task LineRepl_ExitWord_Quits_RegardlessOfCase(string typed)
    {
        var renderer = new ScriptedInputRenderer(typed);
        var agent = new RecordingAgent();

        await DriveAsync(renderer, agent).ConfigureAwait(false);

        await Assert.That(agent.Prompts).IsEmpty()
            .Because(
                $"'{typed}' is a declared exit word, so it must be consumed as a command. " +
                "Forwarding it to the agent both fails to quit and spends the user's " +
                "tokens asking the model to explain the word 'EXIT'.");
        await Assert.That(renderer.ReadCount).IsEqualTo(1)
            .Because(
                $"'{typed}' must end the loop on the read that produced it. A second read " +
                "means the REPL treated the word as a prompt and carried on.");
    }

    /// <summary>
    ///     Non-vacuity for the table above: it is a hand-written list, so it can fall out
    ///     of step with the shared vocabulary — the exact failure mode that produced #684,
    ///     where a second copy of the concept drifted. If a word is added to
    ///     <see cref="ChatCommands.ExitWords" /> without a matching row here, this fails
    ///     and names the gap, instead of the casing of the new word going untested.
    /// </summary>
    [Test]
    public async Task ExitWordCaseTable_CoversEveryDeclaredExitWord()
    {
        var covered = TypedExitWords
            .Select(w => w.ToLowerInvariant())
            .ToHashSet(StringComparer.Ordinal);

        List<string> missing = [.. ChatCommands.ExitWords.Where(w => !covered.Contains(w))];

        await Assert.That(missing).IsEmpty()
            .Because(
                $"Every word in ChatCommands.ExitWords needs at least one row in " +
                $"TypedExitWords. Uncovered: [{string.Join(", ", missing)}].");
    }

    /// <summary>
    ///     Pins the decision this issue turns on. The shared set is matched
    ///     case-insensitively, so the reducer/TUI path quits on <c>QUIT</c>; that is the
    ///     reference behaviour the line REPL is brought into line with, and it is the
    ///     half of the parity that already worked.
    /// </summary>
    [Test]
    public async Task ExitWords_AreMatched_OrdinalIgnoreCase()
    {
        var withCase = TypedExitWords
            .Where(w => !string.Equals(w, w.ToLowerInvariant(), StringComparison.Ordinal))
            .ToArray();

        foreach (string typed in withCase)
        {
            await Assert.That(ChatCommands.ExitWords.Contains(typed)).IsTrue()
                .Because(
                    $"'{typed}' differs from every declared word only in case. Command words in " +
                    "this repo are case-insensitive everywhere else — SlashCommandCatalog looks " +
                    "them up with OrdinalIgnoreCase, ReplCommandCatalog keys its ids the same " +
                    "way, and autocomplete prefix-matches case-insensitively — so exit words " +
                    "must not be the one exception.");
        }
    }

    /// <summary>
    ///     The near miss, so the fix cannot be "make the check broader". Only whole words
    ///     quit; anything else is ordinary input and must still reach the agent.
    /// </summary>
    [Test]
    [Arguments("exi")]
    [Arguments("exi t")]
    [Arguments("exits")]
    [Arguments("qq")]
    public async Task LineRepl_NearMissExitWord_IsForwardedAsPrompt_NotQuit(string typed)
    {
        var renderer = new ScriptedInputRenderer(typed);
        var agent = new RecordingAgent();

        await DriveAsync(renderer, agent).ConfigureAwait(false);

        await Assert.That(agent.Prompts).IsEquivalentTo(new[] { typed })
            .Because(
                $"'{typed}' is not a declared exit word — only whole words quit. Anything " +
                "wider would silently swallow legitimate prompts.");
        await Assert.That(renderer.ReadCount).IsEqualTo(2)
            .Because("The line was consumed as a prompt, so the loop read once more and hit EOF.");
    }

    /// <summary>Runs the line REPL under the hang guard.</summary>
    private static async Task DriveAsync(ScriptedInputRenderer renderer, RecordingAgent agent)
    {
        var agentRegistry = new FakeAgentRegistry(TestAgents.AllowAll());
        var configStore = new JsonConfigStore(
            Path.Combine(Path.GetTempPath(), $"harbor-exitword-{Guid.NewGuid():N}.json"));
        ReplRunner runner = CreateRunner(renderer, agent, agentRegistry, configStore);

        Task<int> repl = runner.RunLineReplAsync(
            Session.Create(Directory.GetCurrentDirectory(), "code", "test-provider", "test-model"));
        await repl.WaitAsync(HangGuard).ConfigureAwait(false);
    }

    private static ReplRunner CreateRunner(
        ITuiRenderer renderer,
        IAgent agent,
        FakeAgentRegistry agentRegistry,
        IConfigStore configStore)
    {
        var authStore = new AuthStore(configStore);
        var wizard = new OnboardingWizard(configStore, authStore);

        // #486: the dispatcher is built by whoever would be the composition root.
        // The line REPL never dispatches a slash command, so these fakes are here
        // only because the dispatcher's collaborators are required ctor params.
        var slashes = new SlashCommandDispatcher(
            NullLogger<SlashCommandDispatcher>.Instance,
            new FakeToolRegistry(),
            new FakeSessionStore(),
            wizard,
            new PermissionService(agentRegistry, NullLogger<PermissionService>.Instance));

        // #486: the adapter too, and for the same reason — the root builds both, and
        // the line REPL dispatches no slash command through either. `providers` is
        // hoisted out of the ReplRunner call because the adapter needs it as well.
        var providers = new FakeProviderRegistry(new ScriptedLlmClient());
        var legacySlash = new LegacySlashRunner(slashes, agentRegistry, configStore, authStore, providers);

        return new ReplRunner(
            NullLogger<ReplRunner>.Instance,
            configStore,
            authStore,
            wizard,
            renderer,
            new FakeEventBus(),
            agent,
            new FakeSessionStore(),
            agentRegistry,
            providers,
            slashes,
            legacySlash,
            NullLoggerFactory.Instance,
            pluginReload: null,
            rendererPipeline: null,
            tokens: null,
            // The CellForge screen graph is only built on the CellForge path; the line REPL
            // never touches it, so a throwing stub is the honest assertion that it does not.
            cellForgeScreens: static () => throw new InvalidOperationException(
                "Line REPL must not resolve the CellForge screen graph."),
            rendererHost: EmptyServiceProvider.Instance);
    }

    // ── Fakes ───────────────────────────────────────────────────────────────────────

    /// <summary>
    ///     Feeds the canned line, then end-of-input forever — the sequence a terminal, a
    ///     pipe, or a redirected file produces.
    /// </summary>
    private sealed class ScriptedInputRenderer : ITuiRenderer
    {
        private readonly Queue<string> _lines;

        public ScriptedInputRenderer(params string[] lines) => _lines = new Queue<string>(lines);

        /// <summary>How many times the REPL asked for input.</summary>
        public int ReadCount { get; private set; }

        public ITuiRenderContext Context { get; } = new CaptureRenderContext();

        public ViewRegistry Views { get; } = new();

        public ViewModelRegistry ViewModels { get; } = new();

        public Task<Result> InitializeAsync(CancellationToken ct = default) => Task.FromResult(Result.Success());

        public Task RenderAsync(AgentEvent @event, CancellationToken ct = default) => Task.CompletedTask;

        public Task<Maybe<string>> ReadLineAsync(string prompt, CancellationToken ct = default)
        {
            ReadCount++;
            return Task.FromResult(_lines.Count > 0
                ? Maybe.From(_lines.Dequeue())
                : Maybe<string>.None);
        }

        public Task<Result> WriteAsync(string text, CancellationToken ct = default) => Task.FromResult(Result.Success());

        public Task<Result> WriteLineAsync(string? text = null, CancellationToken ct = default) =>
            Task.FromResult(Result.Success());

        public Task<Result> ClearAsync(CancellationToken ct = default) => Task.FromResult(Result.Success());

        public void Dispose() { }
    }

    /// <summary>Records what the REPL forwarded to the agent.</summary>
    private sealed class RecordingAgent : IAgent
    {
        private readonly CancellationTokenSource _abortSource = new();

        public List<string> Prompts { get; } = [];

        public CancellationToken AbortToken => _abortSource.Token;

        public void RequestAbort() => _abortSource.Cancel();

        public Maybe<AgentState> State => throw new NotSupportedException("Not used in these tests.");

        public IDisposable Subscribe(Func<AgentEvent, CancellationToken, ValueTask> listener) => new NopDisposable();

        public Task<Result> PromptAsync(UserMessage message, CancellationToken ct = default) =>
            Task.FromResult(Record(message.Content));

        public Task<Result> PromptAsync(string text, CancellationToken ct = default) =>
            Task.FromResult(Record(text));

        public Task WaitForIdleAsync(CancellationToken ct = default) => Task.CompletedTask;

        public void ResetAbortSource() { }

        public void Initialize(Session session, AgentDefinition agent) { }

        public void Steer(AgentMessage message) { }

        public void Dispose() => _abortSource.Dispose();

        private Result Record(string text)
        {
            Prompts.Add(text);
            return Result.Success();
        }

        private sealed class NopDisposable : IDisposable
        {
            public void Dispose() { }
        }
    }

    /// <summary>Resolves nothing — the line REPL only forwards the host to renderers.</summary>
    private sealed class EmptyServiceProvider : IServiceProvider
    {
        public static EmptyServiceProvider Instance { get; } = new();

        public object? GetService(Type serviceType) => null;
    }
}
