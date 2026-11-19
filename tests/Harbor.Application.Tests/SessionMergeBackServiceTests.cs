using CSharpFunctionalExtensions;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Sessions;
using Harbor.Application.Sessions;
using TUnit.Assertions;

namespace Harbor.Application.Tests;

/// <summary>
///     3way merge-back semantics (issue #2660): only the fork's post-boundary
///     delta travels to the parent, and a parent edit of a base message blocks
///     the whole merge with a structured conflict.
/// </summary>
public class SessionMergeBackServiceTests
{
    private sealed class MergeBackFakeStore : ISessionStore
    {
        public readonly Dictionary<string, Session> Sessions = [];
        public readonly Dictionary<string, List<AgentMessage>> Messages = [];

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
            if (!Messages.TryGetValue(sessionId, out var list))
                return Task.FromResult(Result.Failure($"Session '{sessionId}' not found."));
            list.Add(message);
            return Task.FromResult(Result.Success());
        }

        public Task<Result> AppendMessagesBatchAsync(
            string sessionId, IReadOnlyList<AgentMessage> messages, CancellationToken ct = default)
        {
            if (!Messages.TryGetValue(sessionId, out var list))
                return Task.FromResult(Result.Failure($"Session '{sessionId}' not found."));
            list.AddRange(messages);
            return Task.FromResult(Result.Success());
        }

        public Task<Result> UpdateMessageAsync(string sessionId, AgentMessage message, CancellationToken ct = default)
        {
            if (!Messages.TryGetValue(sessionId, out var list))
                return Task.FromResult(Result.Failure($"Session '{sessionId}' not found."));
            for (int i = 0; i < list.Count; i++)
            {
                if (string.Equals(list[i].Id, message.Id, StringComparison.Ordinal))
                {
                    list[i] = message;
                    return Task.FromResult(Result.Success());
                }
            }

            return Task.FromResult(Result.Failure($"Message '{message.Id}' not found in session '{sessionId}'."));
        }

        public Task<Result<IReadOnlyList<AgentMessage>>> GetMessagesAsync(string sessionId, CancellationToken ct = default)
            => Task.FromResult(Messages.TryGetValue(sessionId, out var list)
                ? Result.Success<IReadOnlyList<AgentMessage>>([.. list])
                : Result.Failure<IReadOnlyList<AgentMessage>>($"Session '{sessionId}' not found."));

        public Task<Result> UpdateAsync(Session session, CancellationToken ct = default)
        {
            if (!Sessions.ContainsKey(session.Id))
                return Task.FromResult(Result.Failure("store rejected update"));
            Sessions[session.Id] = session;
            return Task.FromResult(Result.Success());
        }

        public Task<Result> DeleteAsync(string sessionId, CancellationToken ct = default)
        {
            Sessions.Remove(sessionId);
            Messages.Remove(sessionId);
            return Task.FromResult(Result.Success());
        }

        public Task<Result<int>> DeleteMessagesAfterAsync(string sessionId, string messageId, CancellationToken ct = default)
            => Task.FromResult(Result.Failure<int>("not supported by this fake"));

        public Task<Result<SessionMetadata>> GetStatsAsync(string sessionId, CancellationToken ct = default)
            => Task.FromResult(Result.Success(
                Sessions.TryGetValue(sessionId, out var s) ? s.Metadata : SessionMetadata.Empty));

        public Task<Result> UpdateStatsAsync(string sessionId, SessionMetadata metadata, CancellationToken ct = default)
            => Task.FromResult(Result.Success());
    }

    private static UserMessage User(string id, string sessionId, string text, int tick) =>
        new(id, sessionId, DateTimeOffset.UnixEpoch.AddSeconds(tick), text, "code", "model");

    private static async Task<(Session Parent, Session Fork)> SeedAsync(MergeBackFakeStore store)
    {
        var parent = await store.CreateAsync("/tmp", "code", "kilo", "model");
        await store.AppendMessageAsync(parent.Value.Id, User("m1", parent.Value.Id, "one", 1));
        await store.AppendMessageAsync(parent.Value.Id, User("m2", parent.Value.Id, "two", 2));

        var fork = await store.CreateAsync("/tmp", "code", "kilo", "model");
        await store.UpdateAsync(fork.Value with { ParentSessionId = parent.Value.Id });
        await store.AppendMessageAsync(fork.Value.Id, User("m1", fork.Value.Id, "one", 1));
        await store.AppendMessageAsync(fork.Value.Id, User("m2", fork.Value.Id, "two", 2));
        await store.AppendMessageAsync(fork.Value.Id, User("m4", fork.Value.Id, "fork-new", 4));
        return (parent.Value, fork.Value);
    }

    [Test]
    public async Task MergeBackAsync_ParentMovedOn_AppliesOnlyForkDelta()
    {
        var store = new MergeBackFakeStore();
        var (parent, fork) = await SeedAsync(store);
        await store.AppendMessageAsync(parent.Id, User("m3", parent.Id, "parent-new", 3));

        var result = await new SessionMergeBackService().MergeBackAsync(store, fork.Id, "m2");

        await Assert.That(result.IsSuccess).IsTrue();
        SessionMergeBack merge = result.Value;
        await Assert.That(merge.HasConflicts).IsFalse();
        await Assert.That(merge.AppliedCount).IsEqualTo(1);
        await Assert.That(merge.AppliedMessageIds.Count).IsEqualTo(1);
        await Assert.That(merge.AppliedMessageIds[0]).IsEqualTo("m4");
        await Assert.That(merge.Relation.Kind).IsEqualTo(ThreadRelationKind.Fork);
        await Assert.That(merge.Relation.SourceSessionId).IsEqualTo(fork.Id);
        await Assert.That(merge.Relation.TargetSessionId).IsEqualTo(parent.Id);
        await Assert.That(merge.Relation.SourcePoint).IsEqualTo("m2");

        // The parent kept its own post-fork message; only the delta landed,
        // restamped to the parent session, with no full transcript copy.
        IReadOnlyList<AgentMessage> merged = store.Messages[parent.Id];
        await Assert.That(merged.Count).IsEqualTo(4);
        await Assert.That(merged[2].Id).IsEqualTo("m3");
        await Assert.That(merged[3].Id).IsEqualTo("m4");
        await Assert.That(merged[3].SessionId).IsEqualTo(parent.Id);
        await Assert.That(store.Messages[fork.Id].Count).IsEqualTo(3);
    }

    [Test]
    public async Task MergeBackAsync_ParentEditedBaseMessage_ReportsConflictAndWritesNothing()
    {
        var store = new MergeBackFakeStore();
        var (parent, fork) = await SeedAsync(store);
        await store.UpdateMessageAsync(parent.Id, User("m1", parent.Id, "one-edited", 1));

        var result = await new SessionMergeBackService().MergeBackAsync(store, fork.Id, "m2");

        await Assert.That(result.IsSuccess).IsTrue();
        SessionMergeBack merge = result.Value;
        await Assert.That(merge.HasConflicts).IsTrue();
        await Assert.That(merge.Conflicts.Count).IsEqualTo(1);
        await Assert.That(merge.Conflicts[0].MessageId).IsEqualTo("m1");
        await Assert.That(merge.Conflicts[0].BaseText).IsEqualTo("one");
        await Assert.That(merge.Conflicts[0].TargetText).IsEqualTo("one-edited");
        await Assert.That(merge.AppliedCount).IsEqualTo(0);
        await Assert.That(store.Messages[parent.Id].Count).IsEqualTo(2);
    }

    [Test]
    public async Task MergeBackAsync_NonForkSource_FailsWithoutWriting()
    {
        var store = new MergeBackFakeStore();
        var (parent, _) = await SeedAsync(store);

        var result = await new SessionMergeBackService().MergeBackAsync(store, parent.Id, "m1");

        await Assert.That(result.IsFailure).IsTrue();
        await Assert.That(store.Messages[parent.Id].Count).IsEqualTo(2);
    }

    [Test]
    public async Task MergeBackAsync_UnsupportedStrategy_FailsWithoutWriting()
    {
        var store = new MergeBackFakeStore();
        var (_, fork) = await SeedAsync(store);

        var result = await new SessionMergeBackService().MergeBackAsync(store, fork.Id, "m2", "overwrite");

        await Assert.That(result.IsFailure).IsTrue();
        await Assert.That(store.Messages[fork.Id].Count).IsEqualTo(3);
    }
}
