using Harbor.Abstractions.Models;
using Harbor.Abstractions.Sessions;
using Harbor.Storage.Jsonl;
using Harbor.TestKit;
using Microsoft.Extensions.Logging.Abstractions;

namespace Harbor.Storage.Jsonl.Tests;

/// <summary>
///     The goal axis of the JSONL store (#1761): the durable goal a session is
///     working toward is recorded with the session, survives a restart, and
///     stays out of the message history the model reads.
/// </summary>
/// <remarks>
///     The rules these tests pin:
///     <list type="bullet">
///         <item>a goal round-trips: objective, phase, round budget and counter;</item>
///         <item>the goal survives a restart, because it lives in the session file;</item>
///         <item>the newest snapshot wins, and the older ones stay in the log;</item>
///         <item>a stale revision is REFUSED rather than overwriting a newer fact;</item>
///         <item>a session with no goal has none — that is not a failure;</item>
///         <item>the goal never becomes history, so it can never reach the model.</item>
///     </list>
/// </remarks>
public class JsonlSessionGoalTests
{
    /// <summary>
    ///     A string that appears ONLY in the goal, so a test can prove it is
    ///     nowhere in the history.
    /// </summary>
    private const string Marker = "goal-marker-1761";

    private static (JsonlSessionStore Store, CapturingLogger<JsonlSessionStore> Logger) CreateStore()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"harbor-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var logger = new CapturingLogger<JsonlSessionStore>();
        return (new JsonlSessionStore(tempDir, logger), logger);
    }

    private static UserMessage Msg(string sessionId, int i) => new(
        $"msg-{i:D2}-{Guid.NewGuid():N}",
        sessionId,
        DateTimeOffset.UtcNow.AddMilliseconds(i),
        $"message {i}",
        "code",
        "claude");

    private static void Delete(JsonlSessionStore store)
    {
        if (Directory.Exists(store.GetRootDirectory()))
        {
            Directory.Delete(store.GetRootDirectory(), true);
        }
    }

    [Test]
    public async Task AppendGoal_RoundTrips_AndSurvivesARestart()
    {
        var (store, _) = CreateStore();
        try
        {
            var session = (await store.CreateAsync("/goal", "code", "anthropic", "claude-opus-4")).Value;

            var appended = await store.AppendGoalAsync(
                session.Id,
                TestGoals.NewGoal($"fix the {Marker}", GoalPhase.Active, maxRounds: 5, roundsStarted: 2));

            await Assert.That(appended).IsSuccessful();
            await Assert.That(appended.Value.Objective).IsEqualTo($"fix the {Marker}");
            await Assert.That(appended.Value.Phase).IsEqualTo(GoalPhase.Active);
            await Assert.That(appended.Value.MaxRounds).IsEqualTo(5);
            await Assert.That(appended.Value.RoundsStarted).IsEqualTo(2);
            await Assert.That(appended.Value.Revision).IsEqualTo(1);

            // A restart: a NEW store over the same directory reads what the
            // previous process wrote.
            var reopened = new JsonlSessionStore(
                store.GetRootDirectory(),
                NullLogger<JsonlSessionStore>.Instance);

            var read = await reopened.GetGoalAsync(session.Id);

            await Assert.That(read).IsSuccessful();
            await Assert.That(read.Value.HasValue).IsTrue();
            await Assert.That(read.Value.Value.Objective).IsEqualTo($"fix the {Marker}");
            await Assert.That(read.Value.Value.Phase).IsEqualTo(GoalPhase.Active);
            await Assert.That(read.Value.Value.MaxRounds).IsEqualTo(5);
            await Assert.That(read.Value.Value.RoundsStarted).IsEqualTo(2);
        }
        finally
        {
            Delete(store);
        }
    }

    [Test]
    public async Task GetGoal_OnASessionWithNoGoal_IsNoneAndNotAFailure()
    {
        var (store, _) = CreateStore();
        try
        {
            var session = (await store.CreateAsync("/goal", "code", "anthropic", "claude-opus-4")).Value;

            var read = await store.GetGoalAsync(session.Id);

            await Assert.That(read).IsSuccessful()
                .Because("a session without a goal is a state, not an error");
            await Assert.That(read.Value.HasNoValue).IsTrue();
        }
        finally
        {
            Delete(store);
        }
    }

    [Test]
    public async Task AppendGoal_TheNewestSnapshotWins_AndTheOlderOnesStayInTheFile()
    {
        var (store, _) = CreateStore();
        try
        {
            var session = (await store.CreateAsync("/goal", "code", "anthropic", "claude-opus-4")).Value;

            await store.AppendGoalAsync(session.Id, TestGoals.NewGoal($"first {Marker}", GoalPhase.Active, revision: 1));
            await store.AppendGoalAsync(
                session.Id,
                TestGoals.NewGoal($"second {Marker}", GoalPhase.Complete, roundsStarted: 2, revision: 2));

            var read = await store.GetGoalAsync(session.Id);

            await Assert.That(read.Value.HasValue).IsTrue();
            await Assert.That(read.Value.Value.Phase).IsEqualTo(GoalPhase.Complete);
            await Assert.That(read.Value.Value.Objective).IsEqualTo($"second {Marker}");

            // The history is append-only: the first snapshot is still in the
            // file, it is simply not the goal in force any more.
            string file = Path.Combine(store.GetRootDirectory(), $"{session.Id}.jsonl");
            string content = await File.ReadAllTextAsync(file);
            await Assert.That(content).Contains($"first {Marker}")
                .Because("the axis is append-only: an older snapshot is shadowed, not destroyed");
        }
        finally
        {
            Delete(store);
        }
    }

    [Test]
    public async Task AppendGoal_ARevisionThatIsNotTheNextOne_IsRefused()
    {
        var (store, _) = CreateStore();
        try
        {
            var session = (await store.CreateAsync("/goal", "code", "anthropic", "claude-opus-4")).Value;

            await store.AppendGoalAsync(session.Id, TestGoals.NewGoal($"first {Marker}", revision: 1));

            // The same revision again: the store already holds it, and a write
            // that names an older revision must not overwrite the newer fact.
            var stale = await store.AppendGoalAsync(
                session.Id,
                TestGoals.NewGoal($"stale {Marker}", GoalPhase.Complete, revision: 1));

            await Assert.That(stale).IsFailed()
                .Because("a stale revision is refused; the loser of a race re-reads and decides again");

            var read = await store.GetGoalAsync(session.Id);
            await Assert.That(read.Value.Value.Phase).IsEqualTo(GoalPhase.Active)
                .Because("the refused write changed nothing");
            await Assert.That(read.Value.Value.Objective).IsEqualTo($"first {Marker}");
        }
        finally
        {
            Delete(store);
        }
    }

    [Test]
    public async Task TheGoal_NeverBecomesHistory()
    {
        var (store, _) = CreateStore();
        try
        {
            var session = (await store.CreateAsync("/goal", "code", "anthropic", "claude-opus-4")).Value;
            await store.AppendMessageAsync(session.Id, Msg(session.Id, 0));
            await store.AppendGoalAsync(session.Id, TestGoals.NewGoal($"objective {Marker}"));

            var messages = await store.GetMessagesAsync(session.Id);

            await Assert.That(messages).IsSuccessful();
            await Assert.That(messages.Value.Count).IsEqualTo(1)
                .Because("the goal lives in its own records; the message read path skips them");

            foreach (AgentMessage message in messages.Value)
            {
                await Assert.That(message.ToString()).DoesNotContain(Marker)
                    .Because("the goal must never reach the model context");
            }
        }
        finally
        {
            Delete(store);
        }
    }
}
