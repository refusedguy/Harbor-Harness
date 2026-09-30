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
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Harbor.App.Cli.Tests;

/// <summary>
///     #589 — the hang test. Drives the real line REPL to end-of-input and proves
///     the loop returns.
/// </summary>
/// <remarks>
///     <para>
///         <c>ITuiRenderer.ReadLineAsync</c> used to return <c>Result&lt;string&gt;</c>,
///         which forced every implementation to report EOF as
///         <c>Result.Success("")</c>. The line REPL read that as "the user submitted
///         an empty line" and re-prompted forever: a 100%-CPU spin on an
///         already-exhausted stream, reachable from Ctrl-D and from any redirect (so
///         CI and scripts hung too).
///     </para>
///     <para>
///         The <em>signature</em> of that fix is already pinned elsewhere —
///         <c>ReadLineAbsenceContractTests</c> on the notification backend and the
///         <c>TuiReadLineContractRules</c> reflection/scan gate. Both are static
///         checks: they can prove no implementation fabricates a value, and they
///         cannot prove the consumer <b>terminates</b>. That is the gap this file
///         closes, and it is the half that actually hung: only driving the loop
///         observes a loop that never returns.
///     </para>
///     <para>
///         Three things are pinned, and they are not the same thing:
///         <list type="bullet">
///         <item>the loop returns on EOF (<see cref="LineRepl_Eof_ReturnsWithoutSpinning" />);</item>
///         <item>EOF is not an empty line
///         (<see cref="ReadLine_Eof_And_EmptyLine_AreDistinctValues" />);</item>
///         <item>a blank line is skipped rather than treated as the end
///         (<see cref="LineRepl_BlankLines_AreSkipped_AndEof_StillEndsTheLoop" />) —
///         which is what stops "fixing" the hang by exiting on the first blank read.</item>
///         </list>
///     </para>
///     <para>
///         <b>Why the hang guard.</b> These tests drive a loop that, before the fix,
///         never returns. A bare <c>await</c> on it does not fail — it wedges the test
///         shard until the CI job-level timeout, with no stack trace and no indication
///         which test was responsible. <see cref="HangGuard" /> bounds every drive at
///         5 seconds and converts the hang into a named, located failure. It is the
///         repo's existing idiom for exactly this (<c>TaskFireAndForgetTests</c>,
///         <c>LspClientTests</c>, <c>TuiEffectHostConcurrencyTests</c> all use the same
///         <c>WaitAsync(TimeSpan.FromSeconds(5))</c> shape).
///     </para>
/// </remarks>
public class LineReplEofTests
{
    /// <summary>
    ///     Repo-standard hang guard. Long enough that a loaded CI box does not
    ///     false-positive, short enough that a regression is reported as a failed
    ///     test rather than a killed job.
    /// </summary>
    private static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(5);

    /// <summary>
    ///     The regression itself: a renderer that reports EOF on every read must
    ///     let the REPL exit. Before the fix this loop never returned — the
    ///     renderer yielded a successful <c>""</c>, the blank-line check hit
    ///     <c>continue</c>, and the loop spun with zero I/O. This is the only test
    ///     in the #589 set that would have caught that as a hang rather than as a
    ///     value mismatch.
    /// </summary>
    [Test]
    public async Task LineRepl_Eof_ReturnsWithoutSpinning()
    {
        var renderer = new ScriptedInputRenderer();
        int exitCode = await DriveAsync(renderer).ConfigureAwait(false);

        await Assert.That(exitCode).IsEqualTo(0);
        // Exactly one read: the loop consumed the EOF and left. Any larger count
        // means EOF was mistaken for a blank line and re-prompted.
        await Assert.That(renderer.ReadCount).IsEqualTo(1);
    }

    /// <summary>
    ///     The contract, pinned by value rather than inferred from "no hang":
    ///     end-of-input is <see cref="Maybe{T}.None" />, while a line the user
    ///     actually submitted — including an empty one — is
    ///     <see cref="Maybe{T}.From" />. If these ever collapse back into one
    ///     value, the loop above starts spinning again, and this test is what
    ///     says so.
    /// </summary>
    [Test]
    public async Task ReadLine_Eof_And_EmptyLine_AreDistinctValues()
    {
        // Exhausted input — what Ctrl-D and an exhausted pipe both produce.
        var atEof = new ScriptedInputRenderer();
        // One submitted line, then EOF — what a bare Enter on a live stream is.
        var afterBlank = new ScriptedInputRenderer("   ");

        Maybe<string> eof = await atEof.ReadLineAsync("> ").ConfigureAwait(false);
        Maybe<string> blank = await afterBlank.ReadLineAsync("> ").ConfigureAwait(false);

        await Assert.That(eof.HasNoValue).IsTrue();
        await Assert.That(eof.HasValue).IsFalse();

        // A submitted blank line is a *present* value. Collapsing it into None
        // would make the REPL exit on Enter; collapsing None into it is the
        // spin this whole issue is about.
        await Assert.That(blank.HasNoValue).IsFalse();
        await Assert.That(blank.HasValue).IsTrue();
        await Assert.That(blank.GetValueOrDefault("sentinel")).IsEqualTo("   ");
    }

    /// <summary>
    ///     A submitted line still reaches the agent, and only EOF ends the loop
    ///     — guards against "fixing" the hang by exiting on the first blank read.
    /// </summary>
    [Test]
    public async Task LineRepl_BlankLines_AreSkipped_AndEof_StillEndsTheLoop()
    {
        var renderer = new ScriptedInputRenderer("hello", "", "   ");
        var agent = new RecordingAgent();

        int exitCode = await DriveAsync(renderer, agent).ConfigureAwait(false);

        await Assert.That(exitCode).IsEqualTo(0);
        // The three blanks were submitted, so three re-prompts happened on top of
        // the single EOF read.
        await Assert.That(renderer.ReadCount).IsEqualTo(4);
        await Assert.That(agent.Prompts.Count).IsEqualTo(1);
        await Assert.That(agent.Prompts[0]).IsEqualTo("hello");
    }

    /// <summary>
    ///     Runs the line REPL under the hang guard. A regression does not fail
    ///     the assertion below — it fails <em>here</em>, with the loop named.
    /// </summary>
    private static async Task<int> DriveAsync(ScriptedInputRenderer renderer, RecordingAgent? agent = null)
    {
        var agentRegistry = new FakeAgentRegistry(TestAgents.AllowAll());
        var configStore = new JsonConfigStore(Path.Combine(Path.GetTempPath(), $"harbor-eof-{Guid.NewGuid():N}.json"));
        ReplRunner runner = CreateRunner(renderer, agent ?? new RecordingAgent(), agentRegistry, configStore);

        Task<int> repl = runner.RunLineReplAsync(
            Session.Create(Directory.GetCurrentDirectory(), "code", "test-provider", "test-model"));
        return await repl.WaitAsync(HangGuard).ConfigureAwait(false);
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
        // only because the dispatcher's collaborators are required ctor params —
        // which is the point: ReplRunner no longer carries them.
        var slashes = new SlashCommandDispatcher(
            NullLogger<SlashCommandDispatcher>.Instance,
            new FakeToolRegistry(),
            new FakeSessionStore(),
            wizard,
            new PermissionService(agentRegistry, NullLogger<PermissionService>.Instance));

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
            new FakeProviderRegistry(new ScriptedLlmClient()),
            slashes,
            NullLoggerFactory.Instance,
            pluginReload: null,
            rendererPipeline: null,
            tokens: null,
            // The CellForge screen graph is only built on the CellForge path; the
            // line REPL never touches it, so a throwing stub is the honest
            // assertion that it does not.
            cellForgeScreens: static () => throw new InvalidOperationException(
                "Line REPL must not resolve the CellForge screen graph."),
            rendererHost: EmptyServiceProvider.Instance);
    }

    // ── Fakes ──────────────────────────────────────────────────────────────

    /// <summary>
    ///     Feeds canned stdin lines, then end-of-input — the same sequence
    ///     <c>Console.ReadLine()</c> produces for a terminal, a pipe, or a file
    ///     redirect. Once the script is exhausted the renderer returns
    ///     <see cref="Maybe{T}.None" /> forever, which is precisely the state
    ///     that used to hang the REPL.
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

        public Task<Result> PromptAsync(UserMessage message, CancellationToken ct = default)
        {
            Prompts.Add(message.Content);
            return Task.FromResult(Result.Success());
        }

        public Task<Result> PromptAsync(string text, CancellationToken ct = default)
        {
            Prompts.Add(text);
            return Task.FromResult(Result.Success());
        }

        public Task WaitForIdleAsync(CancellationToken ct = default) => Task.CompletedTask;

        public void ResetAbortSource() { }

        public void Initialize(Session session, AgentDefinition agent) { }

        public void Steer(AgentMessage message) { }

        public void Dispose() => _abortSource.Dispose();

        private sealed class NopDisposable : IDisposable
        {
            public void Dispose() { }
        }
    }

    /// <summary>Resolves nothing — the line REPL only forwards the host to renderers.</summary>
    private sealed class EmptyServiceProvider : IServiceProvider
    {
        public static readonly EmptyServiceProvider Instance = new();

        public object? GetService(Type serviceType) => null;
    }
}
