using Harbor.Abstractions.Agents;
namespace Harbor.Application.Agents.Pipeline;
/// <summary>
///     Turn-budget enforcement (audit v2 §3.5 concern #5): a run ends once it has
///     consumed <see cref="AgentDefinition.MaxSteps" /> turns. Extracted from the
///     loop's <c>if (turn &gt;= agent.MaxSteps) break;</c> so the budget rule is
///     independently testable and swappable (e.g. dynamic budgets per session).
/// </summary>
public static class MaxStepsBehavior
{
    /// <summary>
    ///     Whether the run's step budget is exhausted after the given turn
    ///     (1-based). The loop ends the run when this returns
    ///     <see langword="true" />.
    /// </summary>
    public static bool IsExhausted(int turn, AgentDefinition agent) => turn >= agent.MaxSteps;
}
