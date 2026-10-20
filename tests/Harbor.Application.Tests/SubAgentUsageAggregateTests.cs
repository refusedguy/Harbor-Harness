using CSharpFunctionalExtensions;
using Harbor.Abstractions.Agents;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Sessions;
using Harbor.Application.Agents;
using Harbor.TestKit;

using FakeAgentLoopBase = Harbor.TestKit.Generated.FakeAgentLoop; // #1769: generated double

using Microsoft.Extensions.Logging.Abstractions;

namespace Harbor.Application.Tests;

/// <summary>
///     #1555 — the failure-path child-usage aggregate of <see cref="SubAgentRunner" />.
///     A storage failure is a VALUE on the rail, not an exception the runner
///     swallows: the run's own error survives, the caller degrades to zero
///     usage, and the loss is logged instead of silent.
/// </summary>
public class SubAgentUsageAggregateTests
{
    /// <summary>How one history read of the child session behaves.</summary>
    private enum ReadBehavior
    {
        /// <summary>The read succeeds.</summary>
        Succeed,

        /// <summary>The store reports a failure — a value on the rail.</summary>
        FailWithError,

        /// <summary>The store throws — an exception at the boundary.</summary>
        Throw,

        /// <summary>The store throws a cancellation nobody asked for.</summary>
        ThrowCanceled,
    }

    /// <summary>
    ///     The shared in-memory store with ONE script on top: the second
    ///     history read of the spawned child, which is the failure-path usage
    ///     aggregate this file is about. The first read (the fresh-session
    ///     load) keeps succeeding, so the run reaches the failure path at all.
    /// </summary>
    private sealed class ScriptedChildReads : ISessionStore
    {
        private readonly FakeSessionStore _inner = new();
        private string? _childId;
        private int _childReads;

        /// <summary>How the failure-path usage aggregate read behaves.</summary>
        public ReadBehavior AggregateRead { get; set; } = ReadBehavior.Succeed;

        /// <summary>How many times the usage aggregate was read.</summary>
        public int AggregateReadCount { get; private set; }

        /// <summary>The stats records the propagation wrote.</summary>
        public IReadOnlyList<SessionMetadata> UpdatedStats => _inner.UpdatedStats;

        public async Task<Result<Session>> CreateAsync(
            string directory, string agentName, string providerId, string modelId, CancellationToken ct = default)
        {
            Result<Session> created = await _inner.CreateAsync(directory, agentName, providerId, modelId, ct)
                .ConfigureAwait(false);
            return created.Tap(session =>
            {
                _childId = session.Id;
            });
        }

        public Task<Result<Session>> GetAsync(string sessionId, CancellationToken ct = default)
            => _inner.GetAsync(sessionId, ct);

        public Task<Result<IReadOnlyList<Session>>> ListAsync(string? projectId = null, CancellationToken ct = default)
            => _inner.ListAsync(projectId, ct);

        public Task<Result> AppendMessageAsync(string sessionId, AgentMessage message, CancellationToken ct = default)
            => _inner.AppendMessageAsync(sessionId, message, ct);

        public Task<Result> UpdateMessageAsync(string sessionId, AgentMessage message, CancellationToken ct = default)
            => _inner.UpdateMessageAsync(sessionId, message, ct);

        public Task<Result<IReadOnlyList<AgentMessage>>> GetMessagesAsync(string sessionId, CancellationToken ct = default)
        {
            int index = sessionId == _childId ? _childReads++ : -1;
            if (index != 1)
            {
                return _inner.GetMessagesAsync(sessionId, ct);
            }

            AggregateReadCount++;
            return AggregateRead switch
            {
                ReadBehavior.FailWithError => Task.FromResult(
                    Result.Failure<IReadOnlyList<AgentMessage>>("child history unreadable")),
                ReadBehavior.Throw => throw new InvalidOperationException("child history read exploded"),
                ReadBehavior.ThrowCanceled => throw new OperationCanceledException("child history read cancelled"),
                _ => _inner.GetMessagesAsync(sessionId, ct),
            };
        }

        public Task<Result> DeleteAsync(string sessionId, CancellationToken ct = default)
            => _inner.DeleteAsync(sessionId, ct);

        public Task<Result> UpdateAsync(Session session, CancellationToken ct = default)
            => _inner.UpdateAsync(session, ct);

        public Task<Result<SessionMetadata>> GetStatsAsync(string sessionId, CancellationToken ct = default)
            => _inner.GetStatsAsync(sessionId, ct);

        public Task<Result> UpdateStatsAsync(string sessionId, SessionMetadata metadata, CancellationToken ct = default)
            => _inner.UpdateStatsAsync(sessionId, metadata, ct);
    }

    /// <summary>Loop fake: burns one turn, then fails the run.</summary>
    private sealed class FailingLoop : FakeAgentLoopBase
    {
        private readonly Usage _usage;

        public FailingLoop(Usage usage) => _usage = usage;

        public override async Task<Result> RunAsync(ISessionContext session, AgentDefinition agent, CancellationToken ct = default)
        {
            await session.AppendMessageAsync(AgentFixtures.AssistantWithUsage("partial work", _usage), ct)
                .ConfigureAwait(false);
            return Result.Failure("model exploded");
        }
    }

    /// <summary>
    ///     The shared contract of a failed aggregate: the run's own error and
    ///     the resume trailer survive, nothing is charged to the parent, and
    ///     the loss is logged.
    /// </summary>
    private static async Task AssertDegradedToZero(
        Result<SubAgentRunResult> result,
        ScriptedChildReads store,
        CapturingLogger<SubAgentRunner> logger)
    {
        await Assert.That(result.IsFailure).IsTrue();
        await Assert.That(result.Error).Contains("model exploded");
        await Assert.That(result.Error).Contains("[resume-session:");
        await Assert.That(store.AggregateReadCount).IsEqualTo(1);
        await Assert.That(store.UpdatedStats.Count).IsEqualTo(0);
        await Assert.That(logger.Contains("Failed to aggregate sub-agent usage")).IsTrue();
    }

    /// <summary>
    ///     Runs a sub-agent whose run FAILS, against a store whose
    ///     failure-path usage aggregate read behaves as scripted.
    /// </summary>
    private static async Task<(
        Result<SubAgentRunResult> Result,
        ScriptedChildReads Store,
        CapturingLogger<SubAgentRunner> Logger)> RunWithScriptedAggregate(ReadBehavior behavior)
    {
        var store = new ScriptedChildReads { AggregateRead = behavior };
        var logger = new CapturingLogger<SubAgentRunner>();
        var runner = new SubAgentRunner(store, new FailingLoop(new Usage(40, 10)), logger);

        Result<SubAgentRunResult> result = await runner.RunAsync(
            AgentFixtures.SubAgent(), new SubAgentRunRequest("go", ParentSessionId: "parent-1"));
        return (result, store, logger);
    }

    /// <summary>
    ///     The happy path of the aggregate: the child's history is readable,
    ///     so the tokens it burned before the failure reach the parent. (The
    ///     message-level fold is covered by <c>SubAgentCostPropagationTests</c>;
    ///     this store keeps the stats, not the messages.)
    /// </summary>
    [Test]
    public async Task RunAsync_LoopFailure_AggregateReadsHistory_PropagatesPartialUsageToParent()
    {
        var store = new ScriptedChildReads();
        var runner = new SubAgentRunner(
            store, new FailingLoop(new Usage(40, 10)), NullLogger<SubAgentRunner>.Instance);

        var result = await runner.RunAsync(
            AgentFixtures.SubAgent(), new SubAgentRunRequest("go", ParentSessionId: "parent-1"));

        await Assert.That(result.IsFailure).IsTrue();
        await Assert.That(result.Error).Contains("model exploded");
        await Assert.That(store.AggregateReadCount).IsEqualTo(1);
        // The parent's totals carry the child's partial spend.
        await Assert.That(store.UpdatedStats.Count).IsEqualTo(1);
        await Assert.That(store.UpdatedStats[0].TokensInput).IsEqualTo(40);
        await Assert.That(store.UpdatedStats[0].TokensOutput).IsEqualTo(10);
    }

    /// <summary>
    ///     The store REPORTS a failure (a value on the rail): the run's own
    ///     error survives, nothing is charged to the parent, and the loss is
    ///     logged — the old code swallowed this path in silence.
    /// </summary>
    [Test]
    public async Task RunAsync_StoreReportsFailure_AggregateDegradesToZero_OriginalErrorSurvives()
    {
        var (result, store, logger) = await RunWithScriptedAggregate(ReadBehavior.FailWithError);

        await AssertDegradedToZero(result, store, logger);
        await Assert.That(logger.Contains("child history unreadable")).IsTrue();
    }

    /// <summary>
    ///     The store THROWS (an exception at the boundary): the canonical
    ///     handler turns it into a value, the run's own error survives, and
    ///     the exception's message reaches the log.
    /// </summary>
    [Test]
    public async Task RunAsync_StoreThrows_AggregateDegradesToZero_OriginalErrorSurvives()
    {
        var (result, store, logger) = await RunWithScriptedAggregate(ReadBehavior.Throw);

        await AssertDegradedToZero(result, store, logger);
        await Assert.That(logger.Contains("child history read exploded")).IsTrue();
    }

    /// <summary>
    ///     A cancellation is not a storage failure: it propagates instead of
    ///     being downgraded to a quiet zero-usage aggregate (bible §4.5).
    /// </summary>
    [Test]
    public async Task RunAsync_StoreThrowsCancellation_CancellationPropagates()
    {
        var store = new ScriptedChildReads { AggregateRead = ReadBehavior.ThrowCanceled };
        var runner = new SubAgentRunner(
            store, new FailingLoop(new Usage(40, 10)), NullLogger<SubAgentRunner>.Instance);

        await Assert.That(async () => await runner.RunAsync(
            AgentFixtures.SubAgent(),
            new SubAgentRunRequest("go", ParentSessionId: "parent-1")))
            .Throws<OperationCanceledException>();
    }
}
