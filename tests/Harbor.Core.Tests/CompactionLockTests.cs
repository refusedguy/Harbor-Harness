// CompactionLockTests.cs — the compaction lock, end to end (#1767).
//
// THE THREE FACTS THESE TESTS PIN
// -------------------------------
// 1. A WRITE DURING A COMPACTION LANDS INSIDE THE BRACKET. The start record
//    is appended before the summarizer runs and the end record after
//    everything the compaction wrote, so a write that happens while the
//    compaction is in flight cannot pass without the marker.
//
// 2. A FAILED COMPACTION IS RECORDED, NOT SWALLOWED. The end record carries
//    the failure text, so "the compaction failed, and here is why" survives
//    the process.
//
// 3. A FAILED COMPACTION DOES NOT POISON THE SESSION. The lock is released
//    on the failure path too, so the next compaction takes it and runs.

using System.Threading.Channels;
using CSharpFunctionalExtensions;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Sessions;
using Harbor.Application.Agents.Pipeline;
using Harbor.Diagnostics;
using Harbor.TestKit;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;

namespace Harbor.Core.Tests;

/// <summary>
///     The compaction lock of #1767: the bracket in the session log, the
///     failure that stays visible, and the recovery that follows it.
/// </summary>
public class CompactionLockTests
{
    // ── The doubles ──────────────────────────────────────────────────────

    /// <summary>
    ///     A compaction-record seam that RECORDS the order of everything that
    ///     happens to the session — the lock records and the message appends
    ///     alike. That order is the fact under test: a write that happens
    ///     while a compaction is in flight has to land between the start and
    ///     the end.
    /// </summary>
    private sealed class RecordingCompactionStore : ICompactionEventStore
    {
        public List<string> Operations { get; } = [];

        public List<SessionCompactionEvent> Events { get; } = [];

        /// <summary>When set, every append fails — the store cannot record the marker.</summary>
        public bool AppendFails { get; set; }

        public Task<Result> AppendCompactionEventAsync(
            string sessionId,
            SessionCompactionEvent compactionEvent,
            CancellationToken ct = default)
        {
            Operations.Add($"compaction:{compactionEvent.Kind}");

            if (AppendFails)
            {
                return Task.FromResult(Result.Failure("the disk is full"));
            }

            Events.Add(compactionEvent);
            return Task.FromResult(Result.Success());
        }

        public Task<Result<IReadOnlyList<SessionCompactionEvent>>> GetCompactionEventsAsync(
            string sessionId,
            CancellationToken ct = default) =>
            Task.FromResult(Result.Success<IReadOnlyList<SessionCompactionEvent>>([.. Events]));
    }

    /// <summary>
    ///     A session context over the recording store: the message appends go
    ///     into the SAME operation log, so the order of the whole session is
    ///     one list.
    /// </summary>
    private sealed class LockingSessionContext(RecordingCompactionStore store) : ISessionContext
    {
        private readonly List<AgentMessage> _messages = [];

        public Session Session { get; } =
            Session.Create("/tmp/harbor-compaction-lock", "code", "test", "test-model");

        public IReadOnlyList<AgentMessage> Messages => _messages;

        public Channel<AgentMessage> SteeringQueue { get; } = Channel.CreateUnbounded<AgentMessage>();

        public ICompactionEventStore? CompactionEvents => store;

        public Task AppendMessageAsync(AgentMessage message, CancellationToken ct = default)
        {
            store.Operations.Add($"message:{message.Id}");
            _messages.Add(message);
            return Task.CompletedTask;
        }

        public Task UpdateStatsAsync(Usage usage, Pricing pricing, CancellationToken ct = default) =>
            Task.CompletedTask;
    }

    /// <summary>A session context whose store records no compaction events at all.</summary>
    private sealed class LocklessSessionContext : ISessionContext
    {
        private readonly List<AgentMessage> _messages = [];

        public Session Session { get; } =
            Session.Create("/tmp/harbor-compaction-lock", "code", "test", "test-model");

        public IReadOnlyList<AgentMessage> Messages => _messages;

        public Channel<AgentMessage> SteeringQueue { get; } = Channel.CreateUnbounded<AgentMessage>();

        public ICompactionEventStore? CompactionEvents => null;

        public Task AppendMessageAsync(AgentMessage message, CancellationToken ct = default)
        {
            _messages.Add(message);
            return Task.CompletedTask;
        }

        public Task UpdateStatsAsync(Usage usage, Pricing pricing, CancellationToken ct = default) =>
            Task.CompletedTask;
    }

    /// <summary>A compaction service whose run is scripted by the test.</summary>
    private sealed class ScriptedCompactionService(
        Func<CancellationToken, Task<Result<CompactionResult>>> run) : ICompactionService
    {
        public int Calls { get; private set; }

        public bool ShouldCompact(IReadOnlyList<AgentMessage> messages, ModelInfo model) => true;

        public Task<Result<CompactionResult>> CompactAsync(
            string sessionId,
            IReadOnlyList<AgentMessage> messages,
            ModelInfo model,
            CancellationToken ct = default)
        {
            Calls++;
            return run(ct);
        }
    }

    /// <summary>A tracker that always says "compact" — the threshold is not what is under test.</summary>
    private sealed class AlwaysCompactTracker : ITokenTracker
    {
        public void RecordTurnUsage(Usage usage)
        {
        }

        public int Estimate(string text) => 0;

        public int EstimateMessage(AgentMessage message) => 0;

        public int EstimateTokens(IReadOnlyList<AgentMessage> messages) => 0;

        public bool ShouldCompact(IReadOnlyList<AgentMessage> messages, ModelInfo model) => true;

        public TokenStats GetStats() => new(0, 0, null, null, null);
    }

    // ── The fixtures ─────────────────────────────────────────────────────

    private static ModelInfo TestModel() =>
        new("test-model", "test", "Test Model", 1_000_000, 1_024, false, false, true, Pricing.Unknown, "openai");

    private static IReadOnlyList<AgentMessage> History() =>
    [
        new UserMessage("u1", "session-1", DateTimeOffset.UnixEpoch, "hello", "code", "test-model"),
        new AssistantMessage(
            "a1", "session-1", DateTimeOffset.UnixEpoch,
            [new TextPart("hi")], StopReason.Stop, new Usage(0, 0), "test-model"),
    ];

    private static AgentMessage SummaryMessage() =>
        new AssistantMessage(
            "summary-1", "session-1", DateTimeOffset.UnixEpoch,
            [new TextPart("the summary")], StopReason.Stop, new Usage(0, 10), "test-model",
            IsSummary: true, SummaryFirstKeptId: null);

    private static CompactionBehavior Behavior(ICompactionService compaction) =>
        new(
            compaction,
            new AlwaysCompactTracker(),
            new FakeEventBus(),
            NullMetrics.Instance,
            NullLogger.Instance);

    // ── The tests ────────────────────────────────────────────────────────

    /// <summary>
    ///     A write that happens while the compaction is in flight lands
    ///     INSIDE the bracket: after the start record and before the end one.
    /// </summary>
    [Test]
    public async Task A_Write_During_The_Compaction_Lands_Inside_The_Bracket()
    {
        var store = new RecordingCompactionStore();
        var session = new LockingSessionContext(store);
        var summarizerStarted = new TaskCompletionSource();
        var release = new TaskCompletionSource();

        var service = new ScriptedCompactionService(async _ =>
        {
            summarizerStarted.TrySetResult();
            await release.Task.ConfigureAwait(false);
            return Result.Success(new CompactionResult(
                "the summary", 1, 42, TimeSpan.FromMilliseconds(5), SummaryMessage()));
        });

        Task<CompactionOutcome> run = Behavior(service).BeforeTurnAsync(
            session, History(), TestModel(), truncationFallback: false, CancellationToken.None, turn: 4);

        // The summarizer is running: the start record is already in the log.
        await summarizerStarted.Task.ConfigureAwait(false);
        await Assert.That(string.Join("|", store.Operations)).IsEqualTo("compaction:Start");

        // A write that happens NOW must not pass without the marker.
        await session.AppendMessageAsync(
            new UserMessage("concurrent", "session-1", DateTimeOffset.UnixEpoch, "me too", "code", "test-model"),
            CancellationToken.None);

        release.TrySetResult();
        CompactionOutcome outcome = await run.ConfigureAwait(false);

        await Assert.That(outcome.TruncationFallback).IsFalse();
        await Assert.That(string.Join("|", store.Operations))
            .IsEqualTo("compaction:Start|message:concurrent|message:summary-1|compaction:End");
    }

    /// <summary>
    ///     A failed compaction is RECORDED: the end record carries the
    ///     failure text, so the failure is in the log and not only in the
    ///     runtime's warning.
    /// </summary>
    [Test]
    public async Task A_Failed_Compaction_Is_Recorded_With_Its_Reason()
    {
        var store = new RecordingCompactionStore();
        var session = new LockingSessionContext(store);

        var service = new ScriptedCompactionService(_ =>
            Task.FromResult(Result.Failure<CompactionResult>("the summarizer exploded")));

        CompactionOutcome outcome = await Behavior(service).BeforeTurnAsync(
            session, History(), TestModel(), truncationFallback: false, CancellationToken.None, turn: 2);

        await Assert.That(outcome.TruncationFallback).IsTrue();
        await Assert.That(store.Events.Count).IsEqualTo(2);
        await Assert.That(store.Events[0].Kind).IsEqualTo(SessionCompactionEventKind.Start);
        await Assert.That(store.Events[1].Kind).IsEqualTo(SessionCompactionEventKind.End);
        await Assert.That(store.Events[1].Error).IsEqualTo("the summarizer exploded");
        await Assert.That(store.Events[1].Turn).IsEqualTo(2);
    }

    /// <summary>
    ///     The failure does not poison the session: the lock is released, so
    ///     the next compaction takes it and runs.
    /// </summary>
    [Test]
    public async Task A_Session_Recovers_After_A_Failed_Compaction()
    {
        var store = new RecordingCompactionStore();
        var session = new LockingSessionContext(store);

        var failing = new ScriptedCompactionService(_ =>
            Task.FromResult(Result.Failure<CompactionResult>("the summarizer exploded")));

        await Behavior(failing).BeforeTurnAsync(
            session, History(), TestModel(), truncationFallback: false, CancellationToken.None, turn: 1);

        // The lock is free: the failed compaction released it.
        await Assert.That(CompactionLock.FindOpenStart(store.Events)).IsNull();

        var succeeding = new ScriptedCompactionService(_ =>
            Task.FromResult(Result.Success(new CompactionResult(
                "the summary", 1, 42, TimeSpan.FromMilliseconds(5), SummaryMessage()))));

        CompactionOutcome outcome = await Behavior(succeeding).BeforeTurnAsync(
            session, History(), TestModel(), truncationFallback: false, CancellationToken.None, turn: 2);

        await Assert.That(outcome.TruncationFallback).IsFalse();
        await Assert.That(succeeding.Calls).IsEqualTo(1);
        await Assert.That(store.Events.Count).IsEqualTo(4);
        await Assert.That(CompactionLock.FindOpenStart(store.Events)).IsNull();
    }

    /// <summary>
    ///     An ORPHANED lock — a start with no end, which is what a crash in
    ///     the middle of a compaction leaves behind — is reported and carried
    ///     forward, and it does NOT block the session.
    /// </summary>
    [Test]
    public async Task An_Orphaned_Lock_Is_Reported_And_Does_Not_Block()
    {
        var store = new RecordingCompactionStore();
        var session = new LockingSessionContext(store);

        // The evidence a crashed process leaves behind.
        store.Events.Add(new SessionCompactionEvent
        {
            Kind = SessionCompactionEventKind.Start,
            Turn = 9,
            OccurredAt = DateTimeOffset.UnixEpoch,
        });

        var service = new ScriptedCompactionService(_ =>
            Task.FromResult(Result.Success(new CompactionResult(
                "the summary", 1, 42, TimeSpan.FromMilliseconds(5), SummaryMessage()))));

        CompactionOutcome outcome = await Behavior(service).BeforeTurnAsync(
            session, History(), TestModel(), truncationFallback: false, CancellationToken.None, turn: 10);

        await Assert.That(outcome.TruncationFallback).IsFalse();
        await Assert.That(service.Calls).IsEqualTo(1);

        // The new start names the orphan it superseded, so the log keeps the
        // trail of the crash.
        SessionCompactionEvent start = store.Events[1];
        await Assert.That(start.Kind).IsEqualTo(SessionCompactionEventKind.Start);
        await Assert.That(start.OrphanedTurn).IsEqualTo(9);
    }

    /// <summary>
    ///     A store that CANNOT record the marker refuses the compaction: the
    ///     summary would land in the log without the marker that says a
    ///     compaction was under way.
    /// </summary>
    [Test]
    public async Task A_Store_That_Cannot_Record_The_Marker_Refuses_The_Compaction()
    {
        var store = new RecordingCompactionStore { AppendFails = true };
        var session = new LockingSessionContext(store);

        var service = new ScriptedCompactionService(_ =>
            Task.FromResult(Result.Success(new CompactionResult(
                "the summary", 1, 42, TimeSpan.FromMilliseconds(5), SummaryMessage()))));

        CompactionOutcome outcome = await Behavior(service).BeforeTurnAsync(
            session, History(), TestModel(), truncationFallback: false, CancellationToken.None, turn: 3);

        await Assert.That(service.Calls).IsEqualTo(0);
        await Assert.That(outcome.TruncationFallback).IsTrue();
        await Assert.That(string.Join("|", store.Operations)).IsEqualTo("compaction:Start");
    }

    /// <summary>
    ///     A store that has no compaction axis at all degrades instead of
    ///     breaking: the in-process guard is the only exclusion, and the
    ///     compaction still runs.
    /// </summary>
    [Test]
    public async Task A_Store_Without_The_Compaction_Axis_Still_Compacts()
    {
        var session = new LocklessSessionContext();

        var service = new ScriptedCompactionService(_ =>
            Task.FromResult(Result.Success(new CompactionResult(
                "the summary", 1, 42, TimeSpan.FromMilliseconds(5), SummaryMessage()))));

        CompactionOutcome outcome = await Behavior(service).BeforeTurnAsync(
            session, History(), TestModel(), truncationFallback: false, CancellationToken.None, turn: 5);

        await Assert.That(service.Calls).IsEqualTo(1);
        await Assert.That(outcome.TruncationFallback).IsFalse();

        // The summary still reached the session — the degradation is about
        // the RECORD, not about the compaction.
        await Assert.That(session.Messages.Count).IsEqualTo(1);
    }
}
