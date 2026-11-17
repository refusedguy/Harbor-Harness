using CSharpFunctionalExtensions;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Sessions;
using Harbor.Application.Sessions;
using Harbor.TestKit;
using TUnit.Assertions;

namespace Harbor.Application.Tests;

/// <summary>
///     Session fork semantics: prefix copy into a fresh child with durable
///     <c>ParentSessionId</c> lineage; boundary validation fail-closes and a failed
///     lineage stamp leaves no orphaned child behind.
/// </summary>
public class SessionForkServiceTests
{
    private sealed class FakeStore : ISessionStore, ISessionOperationEventStore
    {
        public readonly Dictionary<string, Session> Sessions = [];
        public readonly Dictionary<string, List<AgentMessage>> Messages = [];
        public readonly List<string> DeletedIds = [];
        public readonly List<SessionOperationEvent> Markers = [];
        public readonly List<IReadOnlyList<AgentMessage>> Batches = [];
        public bool FailUpdates;
        public bool FailSingleAppend;

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
        {
            if (FailSingleAppend)
                return Task.FromResult(Result.Failure("fork must use AppendMessagesBatchAsync"));
            if (!Messages.TryGetValue(sessionId, out var list))
                return Task.FromResult(Result.Failure($"Session '{sessionId}' not found."));
            list.Add(message);
            return Task.FromResult(Result.Success());
        }

        public Task<Result> AppendMessagesBatchAsync(
            string sessionId, IReadOnlyList<AgentMessage> messages, CancellationToken ct = default)
        {
            Batches.Add(messages);
            if (!Messages.TryGetValue(sessionId, out var list))
                return Task.FromResult(Result.Failure($"Session '{sessionId}' not found."));
            list.AddRange(messages);
            return Task.FromResult(Result.Success());
        }

        public Task<Result<IReadOnlyList<AgentMessage>>> GetMessagesAsync(string sessionId, CancellationToken ct = default)
            => Task.FromResult(Messages.TryGetValue(sessionId, out var list)
                ? Result.Success<IReadOnlyList<AgentMessage>>([.. list])
                : Result.Failure<IReadOnlyList<AgentMessage>>($"Session '{sessionId}' not found."));

        public Task<Result> UpdateAsync(Session session, CancellationToken ct = default)
        {
            if (FailUpdates || !Sessions.ContainsKey(session.Id))
                return Task.FromResult(Result.Failure("store rejected update"));
            Sessions[session.Id] = session;
            return Task.FromResult(Result.Success());
        }

        public Task<Result> DeleteAsync(string sessionId, CancellationToken ct = default)
        {
            DeletedIds.Add(sessionId);
            Sessions.Remove(sessionId);
            Messages.Remove(sessionId);
            return Task.FromResult(Result.Success());
        }

        public Task<Result> UpdateMessageAsync(string sessionId, AgentMessage message, CancellationToken ct = default)
            => Task.FromResult(Result.Success());

        public Task<Result<int>> DeleteMessagesAfterAsync(string sessionId, string messageId, CancellationToken ct = default)
            => Task.FromResult(Result.Failure<int>("not supported by this fake"));

        public Task<Result<SessionMetadata>> GetStatsAsync(string sessionId, CancellationToken ct = default)
            => Task.FromResult(Result.Success(Sessions.TryGetValue(sessionId, out var s) ? s.Metadata : SessionMetadata.Empty));

        public Task<Result> UpdateStatsAsync(string sessionId, SessionMetadata metadata, CancellationToken ct = default)
            => Task.FromResult(Result.Success());

        public Task<Result> AppendOperationEventAsync(
            string sessionId, SessionOperationEvent operationEvent, CancellationToken ct = default)
        {
            Markers.Add(operationEvent with { Sequence = Markers.Count });
            return Task.FromResult(Result.Success());
        }

        public Task<Result<IReadOnlyList<SessionOperationEvent>>> GetOperationEventsAsync(
            string sessionId, CancellationToken ct = default)
            => Task.FromResult(Result.Success<IReadOnlyList<SessionOperationEvent>>([.. Markers]));
    }

    private static UserMessage User(string sessionId, int n) =>
        new(Guid.NewGuid().ToString("N"), sessionId, DateTimeOffset.UtcNow.AddSeconds(n), $"msg-{n}", "code", "test-model");

    [Test]
    public async Task ForkAsync_FullHistory_ChildCarriesParentLinkAndRestampedCopies()
    {
        var store = new FakeStore();
        var fork = new SessionForkService();
        var (parent, msgIds) = await AddParentAsync(store, 4);

        var result = await fork.ForkAsync(store, parent.Id);

        await Assert.That(result).IsSuccessful();
        var child = result.Value.Session;
        await Assert.That(child.ParentSessionId).IsEqualTo(parent.Id);
        await Assert.That(child.Metadata.MessageCount).IsEqualTo(0);
        var copied = store.Messages[child.Id];
        await Assert.That(copied.Count).IsEqualTo(4);
        await Assert.That(result.Value.Copied).IsEqualTo(4);
        for (int i = 0; i < 4; i++)
        {
            await Assert.That(copied[i].Id).IsEqualTo(msgIds[i]);
            await Assert.That(copied[i].SessionId).IsEqualTo(child.Id);
        }

        // The parent stays untouched.
        await Assert.That(store.Messages[parent.Id].Count).IsEqualTo(4);
        var reloaded = (await store.GetAsync(parent.Id)).Value;
        await Assert.That(reloaded.ParentSessionId).IsNull();
    }

    [Test]
    public async Task ForkAsync_BoundaryAtSecondMessage_ChildGetsPrefixOnly()
    {
        var store = new FakeStore();
        var fork = new SessionForkService();
        var (parent, msgIds) = await AddParentAsync(store, 3);

        var result = await fork.ForkAsync(store, parent.Id, upToMessageId: msgIds[1]);

        await Assert.That(result).IsSuccessful();
        var copied = store.Messages[result.Value.Session.Id];
        await Assert.That(copied.Count).IsEqualTo(2);
        await Assert.That(copied[^1].Id).IsEqualTo(msgIds[1]);
        await Assert.That(store.Messages[parent.Id].Count).IsEqualTo(3);
    }

    [Test]
    public async Task ForkAsync_UnknownBoundary_FailsWithoutCreatingAnything()
    {
        var store = new FakeStore();
        var fork = new SessionForkService();
        var parent = (await AddParentAsync(store, 2)).Session;
        int sessionsBefore = store.Sessions.Count;

        var result = await fork.ForkAsync(store, parent.Id, upToMessageId: "no-such-id");

        await Assert.That(result).IsFailed();
        await Assert.That(store.Sessions.Count).IsEqualTo(sessionsBefore);
    }

    [Test]
    public async Task ForkAsync_MissingSource_FailsWithStoreError()
    {
        var store = new FakeStore();

        var result = await new SessionForkService().ForkAsync(store, "absent");

        await Assert.That(result).IsFailed();
        await Assert.That(result.Error).Contains("absent");
    }

    [Test]
    public async Task ForkAsync_LineageStampFails_NoOrphanChild()
    {
        var store = new FakeStore { FailUpdates = true };
        var fork = new SessionForkService();
        var parent = (await AddParentAsync(store, 1)).Session;

        var result = await fork.ForkAsync(store, parent.Id);

        await Assert.That(result).IsFailed();
        // No half-forked orphan may survive: the shell was created, then rolled back.
        await Assert.That(store.DeletedIds.Count()).IsEqualTo(1);
        await Assert.That(store.Sessions.ContainsKey(store.DeletedIds[0])).IsFalse();
    }

    [Test]
    public async Task ForkAsync_UsesBatchAndBracketsCopyWithMatchedMarkers()
    {
        var store = new FakeStore();
        var parent = (await AddParentAsync(store, 3)).Session;
        var ids = store.Messages[parent.Id].Select(m => m.Id).ToList();
        // Arm this only after seeding: a single-message append now fails, so
        // the fork can only succeed through the batch primitive.
        store.FailSingleAppend = true;

        var result = await new SessionForkService().ForkAsync(store, parent.Id, upToMessageId: ids[1]);

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(store.Batches.Count).IsEqualTo(1);
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

    /// <summary>
    ///     Seed one parent session with <paramref name="count" /> user messages,
    ///     returning the session and the seeded message ids in chronological order.
    /// </summary>
    private static async Task<(Session Session, List<string> MsgIds)> AddParentAsync(FakeStore store, int count)
    {
        var parent = (await store.CreateAsync("/tmp/harbor-fork", "code", "test", "test-model")).Value;
        var ids = new List<string>(count);
        for (int i = 0; i < count; i++)
        {
            var msg = User(parent.Id, i);
            ids.Add(msg.Id);
            await store.AppendMessageAsync(parent.Id, msg);
        }
        return (parent, ids);
    }
}
