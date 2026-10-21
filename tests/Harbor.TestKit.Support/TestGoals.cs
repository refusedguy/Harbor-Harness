// TestGoals.cs — the goal fixture the goal tests share (#1761).
//
// WHY IT IS HERE AND NOT IN A TEST PROJECT
// ----------------------------------------
// Three test projects build a goal: the driver tests (a goal with rounds
// left, a goal whose budget is spent), the tool tests (a goal the agent
// completes, a goal that is already terminal) and the storage tests (a goal
// written to a file). The goal is a FIXTURE, not a double — the double is
// the store that holds it (`FakeGoalStore`) — and a fixture three projects
// need belongs in the kit, the way `TestEvents` and `TestMessages` do.
//
// WHY THE TIMESTAMPS ARE THE FIXTURE'S OWN
// ----------------------------------------
// A goal carries the moment it was set and the moment it last changed. No
// test asserts on them (the store never reads them), so the fixture stamps
// them with the clock it has: a test that wanted a specific moment would
// say so, and the fixture would take it.

using Harbor.Abstractions.Sessions;

namespace Harbor.TestKit;

/// <summary>
///     The goal fixture: a goal with the objective, the phase, the round
///     budget, the counter and the revision a test needs (#1761).
/// </summary>
public static class TestGoals
{
    /// <summary>
    ///     Build a goal.
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
