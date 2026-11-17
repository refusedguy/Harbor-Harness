using CSharpFunctionalExtensions;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Sessions;
using Harbor.Application.Sessions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;

namespace Harbor.Application.Tests;

/// <summary>
///     Crash-marker semantics of #2226: a fork's history copy goes through the
///     batch primitive, and a start marker with no end is detectable as an
///     interrupted fork or an interrupted turn.
/// </summary>
public class SessionCrashMarkerTests
{
    /// <summary>
    ///     A store that records markers and batches. A per-message append is a
    ///     hard failure: the fork must use the batch primitive, not the loop.
    /// </summary>
    private sealed class MarkerStore : ISessionStore, ISessionOperationEventStore
    {
        public readonly Dictionary<string, Session> Sessions = [];
        public readonly Dictionary<string, List<AgentMessage>> Messages = [];
        public readonly List<SessionOperationEvent> Markers = [];
        public readonly List<IReadOnlyList<AgentMessage>> Batches = [];

        public Task<Result<Session>> CreateAsync(
            string directory, string agentName, string providerId, string modelId, CancellationToken ct = default)
        {
            var session = Session.Create(directory, agentName, providerId, modelId);
            Sessions[session.Id] = session;
            Messages[session.Id] = [];
            return Task.FromResult(Result.Success(session));
        }

        public Task<Result<Session>> GetAsync(string sessionId, CancellationToken ct = default)
            => Task.FromResult(Sessions.TryGetValue(sessionId, out var s)
                ? Result.Success(s)
                : Result.Failure<Session>($"Session '{sessionId}' not found."));

        public Task<Result<IReadOnlyList<Session>>> ListAsync(string? projectId = null, CancellationToken ct = default)
            => Task.FromResult(Result.Success<IReadOnlyList<Session>>([.. Sessions.Values]));

        public Task<Result> AppendMessageAsync(string sessionId, AgentMessage message, CancellationToken ct = default)
            => Task.FromResult(Result.Failure("fork must use AppendMessagesBatchAsync"));

        public Task<Result> AppendMessagesBatchAsync(
            string sessionId, IReadOnlyList<AgentMessage> messages, CancellationToken ct = default)
        {
            Batches.Add(messages);
            Messages[sessionId].AddRange(messages);
            return Task.FromResult(Result.Success());
        }

        public Task<Result<IReadOnlyList<AgentMessage>>> GetMessagesAsync(string sessionId, CancellationToken ct = default)
            => Task.FromResult(Messages.TryGetValue(sessionId, out var list)
                ? Result.Success<IReadOnlyList<AgentMessage>>([.. list])
                : Result.Failure<IReadOnlyList<AgentMessage>>($"Session '{sessionId}' not found."));

        public Task<Result> UpdateAsync(Session session, CancellationToken ct = default)
        {
            Sessions[session.Id] = session;
            return Task.FromResult(Result.Success());
        }

        public Task<Result> DeleteAsync(string sessionId, CancellationToken ct = default)
        {
            Sessions.Remove(sessionId);
            Messages.Remove(sessionId);
            return Task.FromResult(Result.Success());
        }

        public Task<Result> UpdateMessageAsync(string sessionId, AgentMessage message, CancellationToken ct = default)
            => Task.FromResult(Result.Success());

        public Task<Result<int>> DeleteMessagesAfterAsync(string sessionId, string messageId, CancellationToken ct = default)
            => Task.FromResult(Result.Failure<int>("not supported"));

        public Task<Result<SessionMetadata>> GetStatsAsync(string sessionId, CancellationToken ct = default)
            => Task.FromResult(Result.Success(Sessions.TryGetValue(sessionId, out var s) ? s.Metadata : SessionMetadata.Empty));

        public Task<Result> UpdateStatsAsync(string sessionId, SessionMetadata metadata, CancellationToken ct = default)
            => Task.FromResult(Result.Success());

        public Task<Result> AppendOperationEventAsync(string sessionId, SessionOperationEvent operationEvent, CancellationToken ct = default)
        {
            Markers.Add(operationEvent with { Sequence = Markers.Count });
            return Task.FromResult(Result.Success());
        }

        public Task<Result<IReadOnlyList<SessionOperationEvent>>> GetOperationEventsAsync(string sessionId, CancellationToken ct = default)
            => Task.FromResult(Result.Success<IReadOnlyList<SessionOperationEvent>>([.. Markers]));
    }

    private static UserMessage User(string sessionId, int n) =>
        new(Guid.NewGuid().ToString("N"), sessionId, DateTimeOffset.UtcNow.AddSeconds(n), $"msg-{n}", "code", "test-model");

    [Test]
    public async Task ForkAsync_UsesBatchAndBracketsTheCopyWithMatchedMarkers()
    {
        var store = new MarkerStore();
        var parent = (await store.CreateAsync("/tmp/harbor-fork", "code", "test", "test-model")).Value;
        var ids = new List<string>();
        for (int i = 0; i < 3; i++)
        {
            var message = User(parent.Id, i);
            ids.Add(message.Id);
            await store.AppendMessagesBatchAsync(parent.Id, [message]);
        }

        var result = await new SessionForkService().ForkAsync(store, parent.Id, upToMessageId: ids[1]);

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(store.Batches.Count).IsEqualTo(4); // 3 seed + 1 fork copy
        await Assert.That(store.Messages[result.Value.Session.Id].Count).IsEqualTo(2);
        var starts = store.Markers.Where(m => m.Kind == SessionOperationKind.ForkStart).ToList();
        var ends = store.Markers.Where(m => m.Kind == SessionOperationKind.ForkEnd).ToList();
        await Assert.That(starts.Count).IsEqualTo(1);
        await Assert.That(ends.Count).IsEqualTo(1);
        await Assert.That(starts[0].BoundaryMessageId).IsEqualTo(ids[1]);
        await Assert.That(starts[0].Copied).IsEqualTo(2);
        await Assert.That(ends[0].Owner).IsEqualTo(starts[0].Owner);
        await Assert.That(SessionOperationMarkers.HasIncompleteFork(store.Markers)).IsFalse();
    }

    [Test]
    public async Task InterruptedFork_StartWithoutEnd_IsDetected()
    {
        IReadOnlyList<SessionOperationEvent> markers =
        [
            new SessionOperationEvent
            {
                Kind = SessionOperationKind.ForkStart,
                Owner = "fork-1",
                BoundaryMessageId = "m2",
                Copied = 2,
                OccurredAt = DateTimeOffset.UtcNow,
            },
        ];

        await Assert.That(SessionOperationMarkers.HasIncompleteFork(markers)).IsTrue();
        IReadOnlyList<SessionOperationEvent> open = SessionOperationMarkers.FindUnmatchedStarts(
            markers, SessionOperationKind.ForkStart, SessionOperationKind.ForkEnd);
        await Assert.That(open.Count).IsEqualTo(1);
        await Assert.That(open[0].BoundaryMessageId).IsEqualTo("m2");
        await Assert.That(open[0].Copied).IsEqualTo(2);
    }

    [Test]
    public async Task InterruptedTurn_StartWithoutEnd_IsDetected()
    {
        IReadOnlyList<SessionOperationEvent> interrupted =
        [
            new SessionOperationEvent { Kind = SessionOperationKind.TurnStart, Turn = 1, OccurredAt = DateTimeOffset.UtcNow },
            new SessionOperationEvent { Kind = SessionOperationKind.TurnEnd, Turn = 1, OccurredAt = DateTimeOffset.UtcNow },
            new SessionOperationEvent { Kind = SessionOperationKind.TurnStart, Turn = 2, OccurredAt = DateTimeOffset.UtcNow },
        ];

        IReadOnlyList<SessionOperationEvent> finished =
        [
            new SessionOperationEvent { Kind = SessionOperationKind.TurnStart, Turn = 1, OccurredAt = DateTimeOffset.UtcNow },
            new SessionOperationEvent { Kind = SessionOperationKind.TurnEnd, Turn = 1, OccurredAt = DateTimeOffset.UtcNow },
        ];

        await Assert.That(SessionOperationMarkers.FindIncompleteTurn(interrupted)).IsEqualTo(2);
        await Assert.That(SessionOperationMarkers.FindIncompleteTurn(finished)).IsNull();
    }
}
