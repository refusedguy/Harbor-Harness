using CSharpFunctionalExtensions;
using Harbor.Abstractions.Agents;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Sessions;
using Harbor.Application.Agents;
using Harbor.Application.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;

namespace Harbor.Application.Tests;

/// <summary>
///     The goal round driver of #1761: the goal's next round is reserved at the
///     agent's QUIESCENCE point, the round prompt is injected into the SAME
///     session, and a goal whose round budget is spent is blocked with a code
///     a caller can act on.
/// </summary>
/// <remarks>
///     <para>
///         The rules these tests pin:
///     </para>
///     <list type="bullet">
///         <item>a session with no goal gets no round — the run ends as it would have;</item>
///         <item>an active goal with rounds left gets one, and the round prompt lands in the session;</item>
///         <item>the round counter is the STORE's, so a second driver continues where the first stopped;</item>
///         <item>a goal whose budget is spent is blocked with the round-limit code;</item>
///         <item>a goal that is complete or already blocked gets no round.</item>
///     </list>
/// </remarks>
public class GoalRoundDriverTests
{
    /// <summary>
    ///     A goal store that holds one goal per session and refuses a stale
    ///     revision, exactly as the durable backend does.
    /// </summary>
    private sealed class FakeGoalStore : ISessionGoalStore
    {
        private readonly Dictionary<string, Goal> _goals = new(StringComparer.Ordinal);

        public Task<Result<Maybe<Goal>>> GetGoalAsync(
            string sessionId, CancellationToken ct = default)
        {
            if (_goals.TryGetValue(sessionId, out Goal? goal))
            {
                return Task.FromResult(Result.Success(Maybe<Goal>.From(goal)));
            }

            return Task.FromResult(Result.Success(Maybe<Goal>.None));
        }

        public Task<Result<Goal>> AppendGoalAsync(
            string sessionId, Goal goal, CancellationToken ct = default)
        {
            int expected = _goals.TryGetValue(sessionId, out Goal? current) ? current.Revision + 1 : 1;
            if (goal.Revision != expected)
            {
                return Task.FromResult(
                    Result.Failure<Goal>($"stale revision {goal.Revision}; expected {expected}."));
            }

            _goals[sessionId] = goal;
            return Task.FromResult(Result.Success(goal));
        }

        public void Seed(string sessionId, Goal goal) => _goals[sessionId] = goal;

        public Goal? Peek(string sessionId) =>
            _goals.TryGetValue(sessionId, out Goal? goal) ? goal : null;
    }

    private static Session NewSession() =>
        Session.Create("/goal-tests", "code", "anthropic", "claude-opus-4");

    private static AgentDefinition NewAgent() => AgentDefinition.CodeDefault("claude-opus-4", "anthropic");

    private static Goal NewGoal(
        string objective = "make the tests pass",
        GoalPhase phase = GoalPhase.Active,
        int maxRounds = 3,
        int roundsStarted = 0,
        int revision = 1) =>
        new(
            objective,
            phase,
            maxRounds,
            roundsStarted,
            revision,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow);

    private static GoalRoundDriver NewDriver(ISessionGoalStore? goals) =>
        new(goals, NullLogger.Instance);

    [Test]
    public async Task ASessionWithNoGoal_GetsNoRound()
    {
        var store = new FakeGoalStore();
        var session = new TestSessionContext(NewSession());
        var driver = NewDriver(store);

        var outcome = await driver.TryReserveRoundAsync(session, NewAgent(), default);

        await Assert.That(outcome).IsEqualTo(GoalRoundDriver.RoundOutcome.None);
        await Assert.That(session.Messages.Count).IsEqualTo(0)
            .Because("a run with no goal must look exactly like the run before goals existed");
    }

    [Test]
    public async Task ADriverWithNoStore_GetsNoRound()
    {
        var session = new TestSessionContext(NewSession());
        var driver = NewDriver(null);

        var outcome = await driver.TryReserveRoundAsync(session, NewAgent(), default);

        await Assert.That(outcome).IsEqualTo(GoalRoundDriver.RoundOutcome.None);
        await Assert.That(session.Messages.Count).IsEqualTo(0);
    }

    [Test]
    public async Task AnActiveGoal_ReservesARound_AndInjectsThePromptIntoTheSameSession()
    {
        var store = new FakeGoalStore();
        Session session = NewSession();
        store.Seed(session.Id, NewGoal("make the tests pass", GoalPhase.Active, maxRounds: 3));

        var context = new TestSessionContext(session);
        var driver = NewDriver(store);

        var outcome = await driver.TryReserveRoundAsync(context, NewAgent(), default);

        await Assert.That(outcome).IsEqualTo(GoalRoundDriver.RoundOutcome.Scheduled);

        Goal? stored = store.Peek(session.Id);
        await Assert.That(stored).IsNotNull();
        await Assert.That(stored!.RoundsStarted).IsEqualTo(1)
            .Because("the round is counted in the durable record, not in the process");
        await Assert.That(stored.Revision).IsEqualTo(2);
        await Assert.That(stored.Phase).IsEqualTo(GoalPhase.Active);

        await Assert.That(context.Messages.Count).IsEqualTo(1)
            .Because("the round continues the conversation: the prompt lands in the same session");
        var prompt = context.Messages[0] as UserMessage;
        await Assert.That(prompt).IsNotNull();
        await Assert.That(prompt!.Content).Contains("make the tests pass")
            .Because("the round prompt carries the objective");
        await Assert.That(prompt.Content).Contains("round 1 of 3")
            .Because("the round prompt says which round this is");
        await Assert.That(prompt.SessionId).IsEqualTo(session.Id);
    }

    [Test]
    public async Task TheRoundCounter_IsTheStores_SoASecondDriverContinuesWhereTheFirstStopped()
    {
        var store = new FakeGoalStore();
        Session session = NewSession();
        store.Seed(session.Id, NewGoal(maxRounds: 3));

        var first = NewDriver(store);
        var firstOutcome = await first.TryReserveRoundAsync(
            new TestSessionContext(session), NewAgent(), default);
        await Assert.That(firstOutcome).IsEqualTo(GoalRoundDriver.RoundOutcome.Scheduled);

        // A "restart": a brand-new driver over the same store. The progress
        // lives in the store, so the second driver picks up at round 2 — a
        // process that died mid-goal does not restart the goal from zero.
        var second = NewDriver(store);
        var secondContext = new TestSessionContext(session);
        var secondOutcome = await second.TryReserveRoundAsync(secondContext, NewAgent(), default);

        await Assert.That(secondOutcome).IsEqualTo(GoalRoundDriver.RoundOutcome.Scheduled);
        await Assert.That(store.Peek(session.Id)!.RoundsStarted).IsEqualTo(2);
        var prompt = secondContext.Messages[0] as UserMessage;
        await Assert.That(prompt!.Content).Contains("round 2 of 3");
    }

    [Test]
    public async Task AGoalWhoseBudgetIsSpent_IsBlockedWithTheRoundLimitCode()
    {
        var store = new FakeGoalStore();
        Session session = NewSession();
        store.Seed(session.Id, NewGoal(maxRounds: 2, roundsStarted: 2, revision: 3));

        var context = new TestSessionContext(session);
        var driver = NewDriver(store);

        var outcome = await driver.TryReserveRoundAsync(context, NewAgent(), default);

        await Assert.That(outcome).IsEqualTo(GoalRoundDriver.RoundOutcome.Blocked);

        Goal? stored = store.Peek(session.Id);
        await Assert.That(stored).IsNotNull();
        await Assert.That(stored!.Phase).IsEqualTo(GoalPhase.Blocked);
        await Assert.That(stored.BlockedCode).IsEqualTo(GoalBlockCode.RoundLimit)
            .Because("the interruption is visible as a CODE, not only as prose");
        await Assert.That(stored.BlockedReason).IsNotNull();

        await Assert.That(context.Messages.Count).IsEqualTo(0)
            .Because("a blocked goal opens no round");
    }

    [Test]
    public async Task ACompleteGoal_GetsNoRound()
    {
        var store = new FakeGoalStore();
        Session session = NewSession();
        store.Seed(session.Id, NewGoal(phase: GoalPhase.Complete));

        var context = new TestSessionContext(session);
        var driver = NewDriver(store);

        var outcome = await driver.TryReserveRoundAsync(context, NewAgent(), default);

        await Assert.That(outcome).IsEqualTo(GoalRoundDriver.RoundOutcome.None);
        await Assert.That(store.Peek(session.Id)!.Phase).IsEqualTo(GoalPhase.Complete)
            .Because("a complete goal is terminal; the driver does not reopen it");
    }

    [Test]
    public async Task ABlockedGoal_GetsNoRound()
    {
        var store = new FakeGoalStore();
        Session session = NewSession();
        store.Seed(session.Id, NewGoal(phase: GoalPhase.Blocked, revision: 2));

        var context = new TestSessionContext(session);
        var driver = NewDriver(store);

        var outcome = await driver.TryReserveRoundAsync(context, NewAgent(), default);

        await Assert.That(outcome).IsEqualTo(GoalRoundDriver.RoundOutcome.None);
        await Assert.That(store.Peek(session.Id)!.Revision).IsEqualTo(2)
            .Because("a blocked goal is terminal; the driver leaves it alone");
    }

    [Test]
    public async Task TheRoundPrompt_NamesTheGoalTool_OnlyWhenTheAgentMayCallIt()
    {
        // #793's shape: a prompt that names a tool the turn was not offered
        // tells the model to call something it cannot call.
        var store = new FakeGoalStore();
        Session session = NewSession();
        store.Seed(session.Id, NewGoal());

        var working = new TestSessionContext(session);
        await NewDriver(store).TryReserveRoundAsync(
            working, AgentDefinition.CodeDefault("claude-opus-4", "anthropic"), default);
        var workingPrompt = working.Messages[0] as UserMessage;
        await Assert.That(workingPrompt!.Content).Contains("`goal` tool")
            .Because("the code agent may call the goal tool, so the prompt says how to report");

        var readOnly = new TestSessionContext(session);
        await NewDriver(store).TryReserveRoundAsync(
            readOnly, AgentDefinition.PlanDefault("claude-opus-4", "anthropic"), default);
        var readOnlyPrompt = readOnly.Messages[0] as UserMessage;
        await Assert.That(readOnlyPrompt!.Content).DoesNotContain("`goal` tool")
            .Because("the plan agent's ruleset does not allow the goal tool, so naming it "
                   + "would point the model at a tool it was never given");
    }
}
