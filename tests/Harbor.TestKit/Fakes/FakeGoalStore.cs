// FakeGoalStore.cs — the in-memory goal store the goal tests share (#1761).
//
// WHY IT IS HERE AND NOT IN A TEST PROJECT
// ----------------------------------------
// Three test projects write a goal store: the driver tests (the round
// driver reads and writes the goal), the tool tests (the model-facing
// `goal` tool reads and writes it) and the storage tests (the durable axis
// writes it to a file). A double that three projects need belongs in the
// kit, the way `TestToolContext` and `TestEvents` do — a copy in each
// project is a hand-maintained contract, and the duplication gate says so.
//
// WHAT THE DOUBLE MODELS
// ----------------------
// The durable backend's two rules, and only those: one goal per session, and
// an append whose revision is not exactly one past the stored one is
// REFUSED. That refusal is the concurrency story of the goal axis (the
// driver counts rounds, the agent completes the goal, and they can write at
// the same moment), so a double that skipped it would let a test pass
// against a behaviour the product does not have.

using CSharpFunctionalExtensions;
using Harbor.Abstractions.Sessions;

namespace Harbor.TestKit;

/// <summary>
///     An in-memory goal store: one goal per session, appended by revision,
///     refusing a stale write exactly as the durable backend does (#1761).
/// </summary>
public sealed class FakeGoalStore : ISessionGoalStore
{
    private readonly Dictionary<string, Goal> _goals = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public Task<Result<Maybe<Goal>>> GetGoalAsync(
        string sessionId, CancellationToken ct = default)
    {
        if (_goals.TryGetValue(sessionId, out Goal? goal))
        {
            return Task.FromResult(Result.Success(Maybe<Goal>.From(goal)));
        }

        return Task.FromResult(Result.Success(Maybe<Goal>.None));
    }

    /// <inheritdoc />
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

    /// <summary>
    ///     Put a goal into the store as if it had been written there.
    /// </summary>
    /// <param name="sessionId">The session the goal belongs to.</param>
    /// <param name="goal">The goal to store.</param>
    public void Seed(string sessionId, Goal goal) => _goals[sessionId] = goal;

    /// <summary>
    ///     The goal in force for a session, or null when the session has none.
    /// </summary>
    /// <param name="sessionId">The session to read.</param>
    /// <returns>The goal, or null.</returns>
    public Goal? Peek(string sessionId) =>
        _goals.TryGetValue(sessionId, out Goal? goal) ? goal : null;

    /// <summary>
    ///     Build a goal for a test: the objective, the phase, the round budget,
    ///     the counter and the revision. The two timestamps are the store's
    ///     business and no test asserts on them.
    /// </summary>
    /// <param name="objective">What the goal is.</param>
    /// <param name="phase">Where the goal stands.</param>
    /// <param name="maxRounds">How many rounds the goal may get.</param>
    /// <param name="roundsStarted">How many rounds it has had.</param>
    /// <param name="revision">The goal's revision counter.</param>
    /// <returns>The goal.</returns>
    public static Goal NewGoal(
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
}
