using CSharpFunctionalExtensions;
using Harbor.Abstractions.Events;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Providers;
using Harbor.Abstractions.Sessions;
using Harbor.Application.Agents.Pipeline;
using Harbor.Application.Sessions;
using Harbor.Application.Tests.Fakes;
using Harbor.TestKit;
using TUnit.Assertions;
using TestSessionContext = Harbor.Application.Tests.Fakes.TestSessionContext;

namespace Harbor.Application.Tests;

/// <summary>
///     #1755 — the reactive branch of compaction: a provider that refuses the
///     request because the context window was exceeded is answered with a
///     compaction and a RETRY of the same request, not with a dead run.
/// </summary>
/// <remarks>
///     <para>
///         The preventive path guesses with a chars/4 heuristic, and the guess
///         is systematically low on real providers — the server tokenizer, the
///         tool schemas, the system prompt and the reasoning blocks are all
///         invisible to it. These tests pin the three things the reactive
///         branch promises: the compaction happens, the turn CONTINUES on the
///         compacted view, and a repetition cannot loop forever.
///     </para>
/// </remarks>
public class ContextOverflowRecovery1755Tests
{
    private const string OverflowMessage = "This model's maximum context length is 8192 tokens.";

    /// <summary>
    ///     A history big enough that folding it into a summary is a MEASURABLE
    ///     reduction, and small enough that the test stays fast.
    /// </summary>
    private static TestSessionContext NewSession(int historyMessages = 8)
    {
        var session = Session.Create("/tmp/harbor-1755-overflow", "code", "test", "test-model");

        var seed = new List<AgentMessage>(historyMessages);
        for (int i = 0; i < historyMessages; i++)
        {
            seed.Add(TestMessages.User(
                new string('x', 400) + $"-message-{i}",
                sessionId: session.Id,
                agent: "user"));
        }

        return new TestSessionContext(session, seed);
    }

    [Test]
    public async Task Overflow_CompactsTheHistoryAndContinuesTheTurn()
    {
        var client = new ScriptedLlmClient(
            [TestEvents.LlmError(OverflowMessage, kind: ProviderErrorKind.ContextOverflow, statusCode: 400)],
            [TestEvents.TextDelta(delta: "recovered"), TestEvents.StepFinish(usage: new Usage(1, 1))]);
        var bus = new FakeEventBus();
        var compaction = new FoldingCompactionService();
        var loop = TestLoops.Create(
            client, new FakeToolRegistry(), new TokenTracker(), compaction, bus);
        var session = NewSession();

        var result = await loop.RunAsync(session, TestAgents.AllowAll());

        await Assert.That(result.IsSuccess).IsTrue()
            .Because("the compaction freed the window, so the turn continues");
        await Assert.That(client.StreamCalls).IsEqualTo(2)
            .Because("the refused request is retried exactly once, from the compacted view");
        await Assert.That(compaction.Calls).IsEqualTo(1)
            .Because("the overflow is the trigger — the compaction runs once, forced");

        // The retry is built from the compacted view, not from the history the
        // provider refused.
        await Assert.That(client.RequestSizes[1]).IsLessThan(client.RequestSizes[0]);

        // The compaction is VISIBLE: the run's history says what happened.
        await Assert.That(bus.Events.OfType<CompactionStartedEvent>().Count()).IsEqualTo(1);
        await Assert.That(bus.Events.OfType<CompactionCompletedEvent>().Count()).IsEqualTo(1);

        // A recovered overflow is not an error: an error event for a request
        // that then succeeded would read as a failure the run did not have.
        await Assert.That(bus.Events.OfType<AgentErrorEvent>().Count()).IsEqualTo(0);
    }

    [Test]
    public async Task Overflow_CompactionThatFreesNothing_KeepsTheOriginalRefusal()
    {
        var client = new ScriptedLlmClient(
            [TestEvents.LlmError(OverflowMessage, kind: ProviderErrorKind.ContextOverflow, statusCode: 400)]);
        var bus = new FakeEventBus();
        var loop = TestLoops.Create(
            client, new FakeToolRegistry(), new TokenTracker(), new NonShrinkingCompactionService(), bus);
        var session = NewSession();

        var result = await loop.RunAsync(session, TestAgents.AllowAll());

        await Assert.That(result.IsFailure).IsTrue();
        await Assert.That(result.Error).Contains(OverflowMessage)
            .Because("the provider's own refusal is what the run reports — the harness does not replace it");
        await Assert.That(result.Error).Contains("context overflow recovery")
            .Because("the reader learns that the harness DID try to compact and what it got for it");
        await Assert.That(client.StreamCalls).IsEqualTo(1)
            .Because("a compaction that freed nothing must not pay for the same refusal twice");
        await Assert.That(bus.Events.OfType<CompactionCompletedEvent>().Count()).IsEqualTo(1);
    }

    [Test]
    public async Task Overflow_CompactionThatFails_EndsTheRunWithAReadableError()
    {
        var client = new ScriptedLlmClient(
            [TestEvents.LlmError(OverflowMessage, kind: ProviderErrorKind.ContextOverflow, statusCode: 400)]);
        var bus = new FakeEventBus();
        var loop = TestLoops.Create(
            client, new FakeToolRegistry(), new TokenTracker(), new FailingCompactionService(), bus);
        var session = NewSession();

        var result = await loop.RunAsync(session, TestAgents.AllowAll());

        await Assert.That(result.IsFailure).IsTrue();
        await Assert.That(result.Error).Contains("the compaction failed")
            .Because("the reason the turn could not continue is the compaction's own failure");
        await Assert.That(client.StreamCalls).IsEqualTo(1);
        await Assert.That(bus.Events.OfType<CompactionFailedEvent>().Count()).IsEqualTo(1)
            .Because("a failed compaction is published, exactly as the preventive path publishes it");
    }

    [Test]
    public async Task Overflow_ThatKeepsOverflowing_StopsAtTheRetryBudget()
    {
        // The client refuses EVERY request: the recovery must not turn that
        // into an endless loop.
        var client = new ScriptedLlmClient(
            [TestEvents.LlmError(OverflowMessage, kind: ProviderErrorKind.ContextOverflow, statusCode: 400)]);
        var bus = new FakeEventBus();
        var loop = TestLoops.Create(
            client, new FakeToolRegistry(), new TokenTracker(), new FoldingCompactionService(), bus);
        var session = NewSession();

        var result = await loop.RunAsync(session, TestAgents.AllowAll());

        await Assert.That(result.IsFailure).IsTrue();
        await Assert.That(client.StreamCalls).IsGreaterThan(1)
            .Because("the recovery DID retry — otherwise the bound below would prove nothing");
        await Assert.That(client.StreamCalls).IsLessThanOrEqualTo(1 + CompactionBehavior.MaxOverflowRetries)
            .Because("the retry budget is the bound that keeps a history that cannot shrink from looping");
        await Assert.That(result.Error).Contains("context overflow recovery")
            .Because("the report says the harness tried and says what it got for it");
    }

    [Test]
    public async Task Overflow_IsNotRetriedByTheTransportPolicy()
    {
        // The overflow is not transient: the SAME request fails the same way,
        // so the retry policy must not spend its budget on it — the reactive
        // branch is the only thing that may retry it, and only after a
        // compaction.
        await Assert.That(ProviderErrors.IsTransient(ProviderErrorKind.ContextOverflow)).IsFalse();
        await Assert.That(ProviderErrors.IsContextOverflow(ProviderErrorKind.ContextOverflow)).IsTrue();
    }

    /// <summary>
    ///     A compaction that folds the head of the history into a short summary
    ///     and keeps the newest message — the shape a real summarizer
    ///     produces, and the one that leaves a measurably smaller view.
    /// </summary>
    private sealed class FoldingCompactionService : ICompactionService
    {
        public int Calls { get; private set; }

        public bool ShouldCompact(IReadOnlyList<AgentMessage> messages, ModelInfo model) => false;

        public Task<Result<CompactionResult>> CompactAsync(
            string sessionId,
            IReadOnlyList<AgentMessage> messages,
            ModelInfo model,
            CancellationToken ct = default)
        {
            Calls++;
            // Everything but the newest message is folded into the summary.
            string? firstKeptId = messages.Count > 0 ? messages[^1].Id : null;
            var summary = TestMessages.Assistant(
                "summary of the folded history",
                sessionId: sessionId,
                model: model.Id,
                isSummary: true,
                summaryFirstKeptId: firstKeptId);

            return Task.FromResult(Result.Success(new CompactionResult(
                "summary of the folded history",
                Math.Max(0, messages.Count - 1),
                1_000,
                TimeSpan.Zero,
                summary)));
        }
    }

    /// <summary>
    ///     A compaction that produces a view BIGGER than the one it compacted —
    ///     the "the summary is bigger than the head it replaced" case. The
    ///     retry must not be granted.
    /// </summary>
    private sealed class NonShrinkingCompactionService : ICompactionService
    {
        public bool ShouldCompact(IReadOnlyList<AgentMessage> messages, ModelInfo model) => false;

        public Task<Result<CompactionResult>> CompactAsync(
            string sessionId,
            IReadOnlyList<AgentMessage> messages,
            ModelInfo model,
            CancellationToken ct = default)
        {
            // The anchor is the FIRST message, so the view keeps the whole
            // history and adds the summary on top of it.
            string? firstKeptId = messages.Count > 0 ? messages[0].Id : null;
            var summary = TestMessages.Assistant(
                "summary of the folded history",
                sessionId: sessionId,
                model: model.Id,
                isSummary: true,
                summaryFirstKeptId: firstKeptId);

            return Task.FromResult(Result.Success(new CompactionResult(
                "summary of the folded history",
                0,
                0,
                TimeSpan.Zero,
                summary)));
        }
    }

    /// <summary>A compaction that fails — the summarizer is down.</summary>
    private sealed class FailingCompactionService : ICompactionService
    {
        public bool ShouldCompact(IReadOnlyList<AgentMessage> messages, ModelInfo model) => false;

        public Task<Result<CompactionResult>> CompactAsync(
            string sessionId,
            IReadOnlyList<AgentMessage> messages,
            ModelInfo model,
            CancellationToken ct = default) =>
            Task.FromResult(Result.Failure<CompactionResult>("summarizer unavailable"));
    }
}
