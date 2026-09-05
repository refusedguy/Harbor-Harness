using CSharpFunctionalExtensions;
using Harbor.Abstractions.Agents;
using Harbor.Abstractions.Events;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Models.Identifiers;
using Harbor.Abstractions.Permissions;
using Harbor.Abstractions.Sessions;
using Harbor.Application.Agents.Pipeline;
using Harbor.TestKit;
using Harbor.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;

using TestSessionContext = Harbor.TestKit.TestSessionContext;
namespace Harbor.Application.Tests;

/// <summary>
///     Isolated tests for the run-level pipeline behaviors (audit v2 §3.5): each
///     behavior is driven with a MOCK <c>next</c> delegate — no real agent loop.
/// </summary>
public class PipelineBehaviorTests
{
    private static PromptRequest NewRequest() => new(
        new TestSessionContext(Session.Create("/tmp/harbor-pipeline-tests", "code", "test", "test-model")),
        new AgentDefinition(
            AgentName.Create("code"),
            "Code",
            "pipeline test agent",
            "test-model",
            "test",
            new PermissionRuleset(new PermissionRule[] { new("*", "*", PermissionAction.Allow) })));

    /// <summary>Mock <c>next</c> that counts invocations and returns a scripted outcome.</summary>
    private static (PipelineNext Next, Func<int> Calls) MockNext(Task<Result>? outcome = null)
    {
        int calls = 0;
        PipelineNext next = (_, _) =>
        {
            Interlocked.Increment(ref calls);
            return outcome ?? Task.FromResult(Result.Success());
        };
        return (next, () => Volatile.Read(ref calls));
    }

    [Test]
    public async Task LoggingBehavior_PassesOutcomeThrough_AndAlwaysLogs()
    {
        var behavior = new LoggingBehavior(NullLogger.Instance);
        var request = NewRequest();
        var (next, calls) = MockNext(Task.FromResult(Result.Failure("boom")));

        Result result = await behavior.HandleAsync(request, next, CancellationToken.None);

        await Assert.That(result.IsFailure).IsTrue();
        await Assert.That(calls()).IsEqualTo(1);
    }

    [Test]
    public async Task LoggingBehavior_CancellationByNext_SurfacesOperationCanceled()
    {
        var behavior = new LoggingBehavior(NullLogger.Instance);
        static Task<Result> Cancelled(PromptRequest request, CancellationToken ct) =>
            Task.FromException<Result>(new OperationCanceledException());

        await Assert.That(async () => await behavior.HandleAsync(NewRequest(), Cancelled, CancellationToken.None))
            .Throws<OperationCanceledException>();
    }

    [Test]
    public async Task PermissionCheckBehavior_NullRuleset_ShortCircuitsWithoutNext()
    {
        var behavior = new PermissionCheckBehavior(NullLogger.Instance);
        var definition = NewRequest().Agent with { Permission = null! };
        var request = new PromptRequest(NewRequest().Session, definition);
        var (next, calls) = MockNext();

        Result result = await behavior.HandleAsync(request, next, CancellationToken.None);

        await Assert.That(result.IsFailure).IsTrue();
        await Assert.That(result.Error).Contains("permission");
        await Assert.That(calls()).IsEqualTo(0);
    }

    [Test]
    public async Task PermissionCheckBehavior_ValidRuleset_CallsNext()
    {
        var behavior = new PermissionCheckBehavior(NullLogger.Instance);
        var (next, calls) = MockNext();

        Result result = await behavior.HandleAsync(NewRequest(), next, CancellationToken.None);

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(calls()).IsEqualTo(1);
    }

    [Test]
    public async Task AgentPipeline_RunsBehaviorsInOrder_TerminalLast()
    {
        var order = new List<string>();
        var inner = new RecordingBehavior(order, "inner");
        var outer = new RecordingBehavior(order, "outer");
        var pipeline = new AgentPipeline([outer, inner]);

        Result result = await pipeline.HandleAsync(NewRequest(), (_, _) =>
        {
            order.Add("terminal");
            return Task.FromResult(Result.Success());
        }, CancellationToken.None);

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(order).IsEquivalentTo(["outer", "inner", "terminal"]);
    }

    [Test]
    public async Task AgentPipeline_ShortCircuit_SkipsDownstreamAndTerminal()
    {
        var order = new List<string>();
        var inner = new RecordingBehavior(order, "inner");
        var blocker = new ShortCircuitBehavior();
        var pipeline = new AgentPipeline([inner, blocker]);

        Result result = await pipeline.HandleAsync(NewRequest(), (_, _) =>
        {
            order.Add("terminal");
            return Task.FromResult(Result.Success());
        }, CancellationToken.None);

        await Assert.That(result.IsFailure).IsTrue();
        await Assert.That(order).IsEquivalentTo(["inner"]);
    }

    private sealed class RecordingBehavior(List<string> order, string name) : IPipelineBehavior
    {
        public Task<Result> HandleAsync(PromptRequest request, PipelineNext next, CancellationToken ct)
        {
            order.Add(name);
            return next(request, ct);
        }
    }

    private sealed class ShortCircuitBehavior : IPipelineBehavior
    {
        public Task<Result> HandleAsync(PromptRequest request, PipelineNext next, CancellationToken ct) =>
            Task.FromResult(Result.Failure("short-circuited"));
    }
}

/// <summary>
///     Isolated tests for the per-turn behaviors: CompactionBehavior, SteeringDrainBehavior,
///     MaxStepsBehavior — driven against fakes, without a real agent loop.
/// </summary>
public class TurnBehaviorTests
{
    private static readonly ModelInfo TestModel = new("test-model", "test", "Test Model", 200_000, 4096, false, false, true, Pricing.Unknown, "openai");

    private static TestSessionContext NewSession(params AgentMessage[] seed) =>
        new(Session.Create("/tmp/harbor-turn-behavior-tests", "code", "test", "test-model"), seed);

    private static AgentMessage UserMsg(string sessionId, string content) => new UserMessage(
        Guid.NewGuid().ToString("N"), sessionId, DateTimeOffset.UtcNow, content, "user", "test-model");

    [Test]
    public async Task CompactionBehavior_UnderThreshold_NoCompactionNoEvents()
    {
        var session = NewSession();
        var compaction = new FakeCompactionService();
        var bus = new FakeEventBus();
        var behavior = new CompactionBehavior(
            compaction, new FakeTokenTracker(shouldCompact: false), bus, NullMetrics.Instance, NullLogger.Instance);

        CompactionOutcome outcome = await behavior.BeforeTurnAsync(
            session, session.Messages, TestModel, truncationFallback: false, CancellationToken.None);

        await Assert.That(compaction.Calls).IsEqualTo(0);
        await Assert.That(bus.Events).IsEmpty();
        await Assert.That(outcome.TruncationFallback).IsFalse();
    }

    [Test]
    public async Task CompactionBehavior_OverThreshold_CompactsAndPublishesCompleted()
    {
        var session = NewSession(UserMsg("s", "a"), UserMsg("s", "b"), UserMsg("s", "c"));
        var summaryMessage = new AssistantMessage(
            Guid.NewGuid().ToString("N"),
            session.Session.Id,
            DateTimeOffset.UtcNow,
            [new TextPart("summary")],
            StopReason.Stop,
            new Usage(0, 0),
            "test-model",
            IsSummary: true);
        var summary = new CompactionResult(
            "summary",
            PrunedMessageCount: 2,
            TokensSaved: 100,
            Duration: TimeSpan.FromMilliseconds(1),
            SummaryMessage: summaryMessage);
        var compaction = new ScriptedCompactionService(Result.Success(summary));
        var bus = new FakeEventBus();
        var behavior = new CompactionBehavior(
            compaction, new FakeTokenTracker(shouldCompact: true), bus, NullMetrics.Instance, NullLogger.Instance);

        CompactionOutcome outcome = await behavior.BeforeTurnAsync(
            session, session.Messages, TestModel, truncationFallback: false, CancellationToken.None);

        await Assert.That(compaction.Calls).IsEqualTo(1);
        await Assert.That(session.Messages.OfType<AssistantMessage>().Any(m => m.IsSummary)).IsTrue();
        await Assert.That(bus.Events.OfType<CompactionStartedEvent>()).HasCount(1);
        await Assert.That(bus.Events.OfType<CompactionCompletedEvent>()).HasCount(1);
        await Assert.That(outcome.TruncationFallback).IsFalse();
        // The compacted view folds the history into [summary]: 3 seeded → 1 summary line.
        await Assert.That(outcome.TurnMessages.Count).IsEqualTo(1);
    }

    [Test]
    public async Task CompactionBehavior_SummarizerFails_EngagesFallbackAndPublishesFailure()
    {
        var session = NewSession();
        var compaction = new ScriptedCompactionService(Result.Failure<CompactionResult>("summarizer down"));
        var bus = new FakeEventBus();
        var behavior = new CompactionBehavior(
            compaction, new FakeTokenTracker(shouldCompact: true), bus, NullMetrics.Instance, NullLogger.Instance);

        CompactionOutcome outcome = await behavior.BeforeTurnAsync(
            session, session.Messages, TestModel, truncationFallback: false, CancellationToken.None);

        await Assert.That(bus.Events.OfType<CompactionFailedEvent>()).HasCount(1);
        await Assert.That(outcome.TruncationFallback).IsTrue();
    }

    [Test]
    public async Task CompactionBehavior_FallbackAlreadyEngaged_NeverRetriesCompaction()
    {
        var session = NewSession();
        var compaction = new ScriptedCompactionService(Result.Failure<CompactionResult>("must not be called"));
        var bus = new FakeEventBus();
        var behavior = new CompactionBehavior(
            compaction, new FakeTokenTracker(shouldCompact: true), bus, NullMetrics.Instance, NullLogger.Instance);

        CompactionOutcome outcome = await behavior.BeforeTurnAsync(
            session, session.Messages, TestModel, truncationFallback: true, CancellationToken.None);

        await Assert.That(compaction.Calls).IsEqualTo(0);
        await Assert.That(bus.Events).IsEmpty();
        await Assert.That(outcome.TruncationFallback).IsTrue();
    }

    [Test]
    public async Task CompactionBehavior_CancellationDuringSummarizer_NoFallback()
    {
        var session = NewSession();
        var compaction = new ScriptedCompactionService(Result.Failure<CompactionResult>("cancelled surface"));
        var bus = new FakeEventBus();
        var behavior = new CompactionBehavior(
            compaction, new FakeTokenTracker(shouldCompact: true), bus, NullMetrics.Instance, NullLogger.Instance);

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        CompactionOutcome outcome = await behavior.BeforeTurnAsync(
            session, session.Messages, TestModel, truncationFallback: false, cts.Token);

        // F17: an Esc during compaction is NOT a summarizer failure — no fallback,
        // no destructive truncation of the session.
        await Assert.That(outcome.TruncationFallback).IsFalse();
        await Assert.That(bus.Events.OfType<CompactionFailedEvent>()).HasCount(0);
    }

    [Test]
    public async Task SteeringDrainBehavior_SameSession_MessagesPersistedAndTracked()
    {
        var session = NewSession();
        var tracker = new RecordingTokenTracker();
        var behavior = new SteeringDrainBehavior(tracker, NullLogger.Instance);
        var steer = UserMsg(session.Session.Id, "steer-1");
        session.EnqueueSteering(steer);

        await behavior.DrainAsync(session, CancellationToken.None);

        await Assert.That(session.Messages.OfType<UserMessage>().Select(m => m.Content)).Contains("steer-1");
        await Assert.That(tracker.Appended).IsEqualTo(1);
    }

    [Test]
    public async Task SteeringDrainBehavior_ForeignSession_DroppedNotPersisted()
    {
        var session = NewSession();
        var tracker = new RecordingTokenTracker();
        var behavior = new SteeringDrainBehavior(tracker, NullLogger.Instance);
        session.EnqueueSteering(UserMsg("some-other-session", "alien"));

        await behavior.DrainAsync(session, CancellationToken.None);

        await Assert.That(session.Messages).IsEmpty();
        await Assert.That(tracker.Appended).IsEqualTo(0);
    }

    [Test]
    public async Task MaxStepsBehavior_BudgetExhaustedAtMaxSteps()
    {
        var agent = NewAgent(maxSteps: 3);
        await Assert.That(MaxStepsBehavior.IsExhausted(2, agent)).IsFalse();
        await Assert.That(MaxStepsBehavior.IsExhausted(3, agent)).IsTrue();
        await Assert.That(MaxStepsBehavior.IsExhausted(4, agent)).IsTrue();
    }

    private static AgentDefinition NewAgent(int maxSteps) => new(
        AgentName.Create("code"),
        "Code",
        "turn-behavior test agent",
        "test-model",
        "test",
        new PermissionRuleset(new PermissionRule[] { new("*", "*", PermissionAction.Allow) }),
        maxSteps);

    /// <summary>Compaction fake with a caller-chosen outcome (unlike <see cref="FakeCompactionService" />).</summary>
    private sealed class ScriptedCompactionService(Result<CompactionResult> outcome) : ICompactionService
    {
        public int Calls { get; private set; }

        public bool ShouldCompact(IReadOnlyList<AgentMessage> messages, ModelInfo model) => true;

        public Task<Result<CompactionResult>> CompactAsync(
            string sessionId, IReadOnlyList<AgentMessage> messages, ModelInfo model, CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult(outcome);
        }
    }

    private sealed class RecordingTokenTracker : ITokenTracker
    {
        public int Appended { get; private set; }

        public void RecordTurnUsage(Usage usage)
        {
        }

        public void RecordAppendedMessage(AgentMessage message) => Appended++;

        public int Estimate(string text) => 0;

        public int EstimateMessage(AgentMessage message) => 0;

        public int EstimateTokens(IReadOnlyList<AgentMessage> messages) => 0;

        public bool ShouldCompact(IReadOnlyList<AgentMessage> messages, ModelInfo model) => false;

        public TokenStats GetStats() => new(0, 0, null, null, null);
    }
}
