using System.Text.Json;
using CSharpFunctionalExtensions;
using Harbor.Abstractions.Sessions;
using Harbor.TestKit;
using Microsoft.Extensions.Logging.Abstractions;

namespace Harbor.Tools.Builtin.Tests;

/// <summary>
///     The goal tool of #1761: the channel through which the agent records its
///     verdict on the session's durable goal.
/// </summary>
/// <remarks>
///     The rules these tests pin:
///     <list type="bullet">
///         <item>"complete" records the goal as complete;</item>
///         <item>"block" records it as blocked, with the agent's own reason;</item>
///         <item>a block without a reason is refused — the reason is the point of the phase;</item>
///         <item>an unknown operation is refused;</item>
///         <item>a session with no goal is refused, not invented;</item>
///         <item>a goal that is already terminal is refused — the phase does not go back;</item>
///         <item>a backend with no goal axis says so instead of pretending.</item>
///     </list>
/// </remarks>
public class GoalToolTests
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

    private const string SessionId = "goal-session";

    private static Goal NewGoal(
        GoalPhase phase = GoalPhase.Active,
        int maxRounds = 3,
        int roundsStarted = 0,
        int revision = 1) =>
        new(
            "make the tests pass",
            phase,
            maxRounds,
            roundsStarted,
            revision,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow);

    private static JsonElement Args(string json) => JsonDocument.Parse(json).RootElement;

    private static GoalTool NewTool(ISessionGoalStore? store) =>
        new(store, NullLogger<GoalTool>.Instance);

    [Test]
    public async Task Complete_RecordsTheGoalAsComplete()
    {
        var store = new FakeGoalStore();
        store.Seed(SessionId, NewGoal());
        var tool = NewTool(store);

        var result = await tool.ExecuteAsync(
            Args("""{"operation":"complete"}"""),
            TestToolContext.Create(sessionId: SessionId),
            default);

        await Assert.That(result.IsError).IsFalse();
        Goal? stored = store.Peek(SessionId);
        await Assert.That(stored).IsNotNull();
        await Assert.That(stored!.Phase).IsEqualTo(GoalPhase.Complete);
        await Assert.That(stored.BlockedCode).IsNull()
            .Because("a complete goal is not a blocked one");
    }

    [Test]
    public async Task Block_RecordsTheGoalAsBlocked_WithTheAgentsOwnReason()
    {
        var store = new FakeGoalStore();
        store.Seed(SessionId, NewGoal());
        var tool = NewTool(store);

        var result = await tool.ExecuteAsync(
            Args("""{"operation":"block","reason":"the CI service is unreachable"}"""),
            TestToolContext.Create(sessionId: SessionId),
            default);

        await Assert.That(result.IsError).IsFalse();
        Goal? stored = store.Peek(SessionId);
        await Assert.That(stored).IsNotNull();
        await Assert.That(stored!.Phase).IsEqualTo(GoalPhase.Blocked);
        await Assert.That(stored.BlockedCode).IsEqualTo(GoalBlockCode.AgentReported);
        await Assert.That(stored.BlockedReason).IsEqualTo("the CI service is unreachable");
    }

    [Test]
    public async Task Block_WithoutAReason_IsRefused()
    {
        var store = new FakeGoalStore();
        store.Seed(SessionId, NewGoal());
        var tool = NewTool(store);

        var validation = tool.ValidateArguments(Args("""{"operation":"block"}"""));

        await Assert.That(validation.IsFailure).IsTrue();
        await Assert.That(store.Peek(SessionId)!.Phase).IsEqualTo(GoalPhase.Active)
            .Because("a refused call changes nothing");
    }

    [Test]
    public async Task AnUnknownOperation_IsRefused()
    {
        var store = new FakeGoalStore();
        store.Seed(SessionId, NewGoal());
        var tool = NewTool(store);

        var validation = tool.ValidateArguments(Args("""{"operation":"finished"}"""));

        await Assert.That(validation.IsFailure).IsTrue();
    }

    [Test]
    public async Task AMissingOperation_IsRefused()
    {
        var store = new FakeGoalStore();
        store.Seed(SessionId, NewGoal());
        var tool = NewTool(store);

        var validation = tool.ValidateArguments(Args("{}"));

        await Assert.That(validation.IsFailure).IsTrue();
    }

    [Test]
    public async Task ASessionWithNoGoal_IsRefused_RatherThanInvented()
    {
        var store = new FakeGoalStore();
        var tool = NewTool(store);

        var result = await tool.ExecuteAsync(
            Args("""{"operation":"complete"}"""),
            TestToolContext.Create(sessionId: SessionId),
            default);

        await Assert.That(result.IsError).IsTrue();
        await Assert.That(store.Peek(SessionId)).IsNull()
            .Because("a tool call does not create a goal — the objective is the user's");
    }

    [Test]
    public async Task AGoalThatIsAlreadyComplete_IsRefused()
    {
        var store = new FakeGoalStore();
        store.Seed(SessionId, NewGoal(phase: GoalPhase.Complete));
        var tool = NewTool(store);

        var result = await tool.ExecuteAsync(
            Args("""{"operation":"block","reason":"too late"}"""),
            TestToolContext.Create(sessionId: SessionId),
            default);

        await Assert.That(result.IsError).IsTrue();
        await Assert.That(store.Peek(SessionId)!.Phase).IsEqualTo(GoalPhase.Complete)
            .Because("a terminal phase does not go back");
    }

    [Test]
    public async Task ABackendWithNoGoalAxis_SaysSo()
    {
        var tool = NewTool(null);

        var result = await tool.ExecuteAsync(
            Args("""{"operation":"complete"}"""),
            TestToolContext.Create(sessionId: SessionId),
            default);

        await Assert.That(result.IsError).IsTrue();
        await Assert.That(result.Output).Contains("no goals")
            .Because("pretending a phase that was never set is the lie this axis exists to remove");
    }
}
