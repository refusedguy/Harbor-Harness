using Harbor.Abstractions.Models;
using Harbor.App.Cli.Commands;
using Harbor.Application.Sessions;
using Harbor.Storage.Memory;

namespace Harbor.App.Cli.Tests;

/// <summary>
///     Behavior of the CLI consumer wired in #3407: the fork's post-boundary
///     delta reaches the parent, and a conflict blocks the write entirely.
/// </summary>
public class SessionMergeBackRunnerTests
{
    private readonly MemorySessionStore _store = new();

    private static UserMessage Msg(string id, string sessionId, string text, int tick) =>
        new(id, sessionId, DateTimeOffset.UnixEpoch.AddSeconds(tick), text, "code", "model");

    private async Task<(Session Parent, Session Fork)> SeedAsync()
    {
        Session parent = await SessionFixtures.SeedAsync(
            _store, "root", Msg("m1", "", "one", 1), Msg("m2", "", "two", 2));
        Session fork = await SessionFixtures.SeedAsync(
            _store, "fork",
            Msg("m1", "", "one", 1),
            Msg("m2", "", "two", 2),
            Msg("m4", "", "fork-new", 4));
        Session header = (await _store.GetAsync(fork.Id)).Value;
        await _store.UpdateAsync(header with { ParentSessionId = parent.Id });
        return (parent, fork);
    }

    private SessionMergeBackRunner Runner => new(_store, new SessionMergeBackService());

    [Test]
    public async Task MergeBack_DeltaCrossesToParent_OnlyAfterBoundary()
    {
        var (parent, fork) = await SeedAsync();
        await _store.AppendMessageAsync(parent.Id, Msg("m3", parent.Id, "parent-new", 3));

        var merged = await Runner.MergeBackAsync(fork.Id, "m2");

        await Assert.That(merged.IsSuccess).IsTrue();
        SessionMergeBackRunner.MergeBackOutcome outcome = merged.Value;
        await Assert.That(outcome.HasConflicts).IsFalse();
        await Assert.That(outcome.AppliedCount).IsEqualTo(1);
        await Assert.That(outcome.AppliedMessageIds[0]).IsEqualTo("m4");
        await Assert.That(outcome.Relation.Kind).IsEqualTo(ThreadRelationKind.Fork);
        await Assert.That(outcome.Relation.SourcePoint).IsEqualTo("m2");
        await Assert.That(outcome.Relation.TargetSessionId).IsEqualTo(parent.Id);

        IReadOnlyList<AgentMessage> parentMessages = (await _store.GetMessagesAsync(parent.Id)).Value;
        await Assert.That(parentMessages.Count).IsEqualTo(4);
        await Assert.That(parentMessages[2].Id).IsEqualTo("m3");
        await Assert.That(parentMessages[3].Id).IsEqualTo("m4");
        await Assert.That(parentMessages[3].SessionId).IsEqualTo(parent.Id);
        await Assert.That((await _store.GetMessagesAsync(fork.Id)).Value.Count).IsEqualTo(3);
    }

    [Test]
    public async Task MergeBack_BaseConflict_WritesNothingToParent()
    {
        var (parent, fork) = await SeedAsync();
        await _store.UpdateMessageAsync(parent.Id, Msg("m1", parent.Id, "one-edited", 1));

        var merged = await Runner.MergeBackAsync(fork.Id, "m2");

        await Assert.That(merged.IsSuccess).IsTrue();
        SessionMergeBackRunner.MergeBackOutcome outcome = merged.Value;
        await Assert.That(outcome.HasConflicts).IsTrue();
        await Assert.That(outcome.Conflicts.Count).IsEqualTo(1);
        await Assert.That(outcome.Conflicts[0].MessageId).IsEqualTo("m1");
        await Assert.That(outcome.Conflicts[0].TargetText).IsEqualTo("one-edited");
        await Assert.That(outcome.AppliedCount).IsEqualTo(0);

        IReadOnlyList<AgentMessage> parentMessages = (await _store.GetMessagesAsync(parent.Id)).Value;
        await Assert.That(parentMessages.Count).IsEqualTo(2);
        await Assert.That(parentMessages.Any(m => m.Id == "m4")).IsFalse();
    }
}
