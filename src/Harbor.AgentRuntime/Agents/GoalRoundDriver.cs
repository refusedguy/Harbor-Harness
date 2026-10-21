// GoalRoundDriver.cs — the round driver of #1761.
//
// WHAT THIS IS
// ------------
// The agent's own turn boundary is where a goal round is reserved and where
// the round prompt is injected. There is NO second loop here: the driver does
// not run turns, it decides whether the EXISTING loop should keep going.
//
// WHY THE DRIVER RESERVES A ROUND INSTEAD OF RUNNING ONE
// -------------------------------------------------------
// The run/limit contour already exists (#41: `AgentLoop`, `RunLimitKind`,
// `RunOutcome`) and it already owns the questions "how long may this run
// work" and "why did it stop". A goal adds exactly one thing to it: the
// question "is the WORK finished". So the goal rides the loop instead of
// replacing it — a goal that runs out of rounds ends the run with
// `RunLimitKind.MaxGoalRounds`, the same shape as every other bounded run.
//
// THE ROUND IS NOT STARTED UNTIL THE AGENT GOES QUIET
// ---------------------------------------------------
// A round is reserved at the QUIESCENCE point — the turn in which the model
// stopped asking for tools. That is the only moment at which "the agent has
// nothing left to do on its own" is a fact rather than a guess, and reserving
// a round earlier would spend rounds on a turn that was still working.
//
// THE COUNTER IS THE DURABLE PART
// -------------------------------
// The round counter lives in the goal record, which lives in the session log.
// So the progress of a goal survives a restart: a process that died after two
// rounds opens the session and finds the goal at "2 of 5", not at "0 of 5".
// The ACTIVATION (whether a driver is running right now) is deliberately not
// durable — "the process restarted" must not read as "the goal was cancelled".

using Harbor.Abstractions.Models;
using Harbor.Abstractions.Sessions;
using Microsoft.Extensions.Logging;

namespace Harbor.Application.Agents;

/// <summary>
///     The round driver of #1761: it reserves a goal's next round at the
///     agent's quiescence point and injects the round prompt into the SAME
///     session, so the round continues the conversation rather than starting
///     a new one.
/// </summary>
/// <remarks>
///     <para>
///         <b>Two writers, one revision.</b> The driver counts rounds and the
///         agent completes or blocks the goal, and both write the goal record.
///         The revision counter decides the race: a write that names an older
///         revision is refused, and the loser re-reads and decides again
///         instead of overwriting the newer fact.
///     </para>
///     <para>
///         <b>No goal store, no rounds.</b> A store without the goal axis has
///         no goals, and a driver without a store is inert: the run ends
///         exactly where it would have ended. That is the shape of every
///         optional dependency in this codebase — absent means "the old
///         behaviour", never "a broken one".
///     </para>
///     <para>
///         <b>Why it is public.</b> It is a collaborator of the loop, in the
///         same family as <see cref="ToolDispatcher" /> and
///         <see cref="RunBudgetTracker" />: the type a host composes the
///         agent's behaviour out of. It is public because the test that pins
///         its decisions has to reach it — and opening this assembly's
///         internals to that test project would leak the ZLinq drop-in
///         extensions (a global-namespace type in this assembly) into the
///         test compilation, where they shadow <c>System.Linq</c> and break
///         every LINQ call site in it.
///     </para>
/// </remarks>
public sealed class GoalRoundDriver
{
    /// <summary>
    ///     What the driver decided at a quiescence point.
    /// </summary>
    public enum RoundOutcome
    {
        /// <summary>
        ///     No round was reserved: the session has no goal, the goal is not
        ///     active, or the driver could not record what it decided. The run
        ///     ends the way it would have ended without a goal.
        /// </summary>
        None,

        /// <summary>
        ///     A round was reserved and its prompt injected into the session.
        ///     The run continues with another turn.
        /// </summary>
        Scheduled,

        /// <summary>
        ///     The goal's round budget was spent, and the goal was recorded as
        ///     blocked. The run ends with
        ///     <see cref="RunLimitKind.MaxGoalRounds" />.
        /// </summary>
        Blocked,
    }

    /// <summary>
    ///     The number of times the driver re-reads a goal whose write was
    ///     refused as stale. Two is enough: the second refusal means another
    ///     writer is winning every race, and the honest answer to that is to
    ///     stop rather than to spin.
    /// </summary>
    private const int MaxAttempts = 2;

    /// <summary>
    ///     The goal store. Null when the host's store has no goal axis.
    /// </summary>
    private readonly ISessionGoalStore? _goals;

    /// <summary>
    ///     Logger of the owning loop — the log category stays the loop's.
    /// </summary>
    private readonly ILogger _logger;

    /// <summary>
    ///     Bind the driver to its store.
    /// </summary>
    /// <param name="goals">The goal store, or null when the host has none.</param>
    /// <param name="logger">Logger of the owning loop.</param>
    public GoalRoundDriver(ISessionGoalStore? goals, ILogger logger)
    {
        _goals = goals;
        _logger = logger;
    }

    /// <summary>
    ///     Decide what happens at a quiescence point: reserve the goal's next
    ///     round, block the goal when its budget is spent, or do nothing.
    /// </summary>
    /// <param name="session">The session the run is in.</param>
    /// <param name="agent">The agent definition driving the run.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>What the driver decided.</returns>
    public async Task<RoundOutcome> TryReserveRoundAsync(
        ISessionContext session,
        AgentDefinition agent,
        CancellationToken ct)
    {
        // A local, not the field: the null check below is the one place that
        // knows the store is there, and every use after it is through a
        // non-nullable reference.
        ISessionGoalStore? maybeGoals = _goals;
        if (maybeGoals is null)
        {
            return RoundOutcome.None;
        }

        ISessionGoalStore goals = maybeGoals;

        for (int attempt = 0; attempt < MaxAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();

            Result<Maybe<Goal>> read =
                await goals.GetGoalAsync(session.Session.Id, ct).ConfigureAwait(false);
            if (read.IsFailure || read.Value.HasNoValue)
            {
                // No goal, or a store that cannot answer. Both mean the same
                // thing to the run: there is nothing to continue.
                return RoundOutcome.None;
            }

            Goal goal = read.Value.Value;

            // A goal that is complete or blocked is terminal: the run ends
            // here, and the phase is the reason why.
            if (goal.Phase is not GoalPhase.Active)
            {
                return RoundOutcome.None;
            }

            if (goal.RoundsStarted >= goal.MaxRounds)
            {
                return await BlockAsync(goals, session, goal, ct).ConfigureAwait(false);
            }

            Goal reserved = goal with
            {
                RoundsStarted = goal.RoundsStarted + 1,
                Revision = goal.Revision + 1,
                UpdatedAt = DateTimeOffset.UtcNow,
            };

            Result<Goal> appended =
                await goals.AppendGoalAsync(session.Session.Id, reserved, ct).ConfigureAwait(false);
            if (appended.IsFailure)
            {
                // Somebody else wrote the goal between the read and the write.
                // Re-read and decide again — the newer fact wins.
                GoalRoundDriverLog.RoundWriteRefused(_logger, session.Session.Id, appended.Error);
                continue;
            }

            var prompt = new UserMessage(
                Guid.NewGuid().ToString("N"),
                session.Session.Id,
                DateTimeOffset.UtcNow,
                RenderRoundPrompt(reserved, reserved.RoundsStarted, CanReport(agent)),
                agent.Name.Value,
                agent.Model);
            await session.AppendMessageAsync(prompt, ct).ConfigureAwait(false);

            GoalRoundDriverLog.RoundReserved(
                _logger, session.Session.Id, reserved.RoundsStarted, reserved.MaxRounds);

            return RoundOutcome.Scheduled;
        }

        GoalRoundDriverLog.RoundGiveUp(_logger, session.Session.Id);
        return RoundOutcome.None;
    }

    /// <summary>
    ///     Record that the goal's round budget is spent: the goal becomes
    ///     blocked with the round-limit code, and the run ends with the
    ///     matching limit.
    /// </summary>
    /// <param name="goals">The goal store.</param>
    /// <param name="session">The session the run is in.</param>
    /// <param name="goal">The goal in force, as read.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    ///     <see cref="RoundOutcome.Blocked" /> when the block was recorded;
    ///     <see cref="RoundOutcome.None" /> when it could not be, because a
    ///     goal that was never recorded as blocked must not be reported as one.
    /// </returns>
    private async Task<RoundOutcome> BlockAsync(
        ISessionGoalStore goals,
        ISessionContext session,
        Goal goal,
        CancellationToken ct)
    {
        Goal blocked = goal with
        {
            Phase = GoalPhase.Blocked,
            BlockedCode = GoalBlockCode.RoundLimit,
            BlockedReason =
                $"The goal's round budget of {goal.MaxRounds} round(s) was spent.",
            Revision = goal.Revision + 1,
            UpdatedAt = DateTimeOffset.UtcNow,
        };

        Result<Goal> appended =
            await goals.AppendGoalAsync(session.Session.Id, blocked, ct).ConfigureAwait(false);
        if (appended.IsFailure)
        {
            GoalRoundDriverLog.BlockWriteRefused(_logger, session.Session.Id, appended.Error);
            return RoundOutcome.None;
        }

        GoalRoundDriverLog.GoalBlocked(
            _logger, session.Session.Id, goal.RoundsStarted, goal.MaxRounds);

        return RoundOutcome.Blocked;
    }

    /// <summary>
    ///     Whether the agent may call the <c>goal</c> tool — the channel
    ///     through which it reports its verdict on the goal.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The predicate is the resolver's own: a tool is offered to a
    ///         turn exactly when its ruleset ALLOWS it
    ///         (<c>ToolRegistry.ResolveTools</c>). A round prompt that named
    ///         a tool the turn was not offered is the #793 defect — the model
    ///         is told to call something it cannot call — so the prompt asks
    ///         the same question the resolver asks.
    ///     </para>
    ///     <para>
    ///         An agent that cannot report keeps working: the round prompt
    ///         then carries the WORK and not the verdict, and the goal ends
    ///         the honest way — blocked by the round limit, with the code that
    ///         says why.
    ///     </para>
    /// </remarks>
    /// <param name="agent">The agent definition driving the run.</param>
    /// <returns>True when the agent may call the goal tool.</returns>
    private static bool CanReport(AgentDefinition agent) =>
        agent.Permission.Evaluate(GoalToolName, "*") == PermissionAction.Allow;

    /// <summary>
    ///     The name of the model-facing tool that records the agent's verdict
    ///     on the goal.
    /// </summary>
    private const string GoalToolName = "goal";

    /// <summary>
    ///     Render the prompt that starts a round.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The wording is not decoration. Two of its clauses are load
    ///         bearing:
    ///     </para>
    ///     <list type="bullet">
    ///         <item>
    ///             <term>"treat the current state as authoritative"</term>
    ///             <description>
    ///                 The earlier rounds' narration is a CLAIM about the
    ///                 workspace; the workspace is the evidence. A round that
    ///                 trusts the narration repeats work that was undone.
    ///             </description>
    ///         </item>
    ///         <item>
    ///             <term>"inspect them instead of assuming"</term>
    ///             <description>
    ///                 The same rule ADR-020 applies to the per-turn change
    ///                 oracle: the harness measures, the model reports, and
    ///                 the two must not be confused.
    ///             </description>
    ///         </item>
    ///     </list>
    /// </remarks>
    /// <param name="goal">The goal, with the round already counted.</param>
    /// <param name="round">The 1-based number of the round that starts now.</param>
    /// <param name="canReport">
    ///     Whether the agent may call the goal tool. The prompt names the tool
    ///     only then — a prompt that named a tool the turn was not offered is
    ///     the #793 defect.
    /// </param>
    /// <returns>The prompt text.</returns>
    internal static string RenderRoundPrompt(Goal goal, int round, bool canReport) =>
        $"Goal (round {round} of {goal.MaxRounds}): {goal.Objective}\n\n"
        + "Continue working toward the objective in this same session. Treat the current "
        + "workspace, tool results, and durable session state as authoritative; inspect them "
        + "instead of assuming earlier narration is still current. Make concrete progress and "
        + "verify the result."
        + (canReport
            ? "\n\nWhen the objective is met, call the `goal` tool with operation \"complete\". "
              + "If you cannot proceed and know what is missing, call it with operation \"block\" "
              + "and a reason."
            : string.Empty);
}

/// <summary>
///     SG2: BCL <c>[LoggerMessage]</c> delegates for <see cref="GoalRoundDriver" />.
/// </summary>
internal static partial class GoalRoundDriverLog
{
    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "Goal round {Round} of {MaxRounds} reserved for session {SessionId}")]
    public static partial void RoundReserved(ILogger logger, string sessionId, int round, int maxRounds);

    [LoggerMessage(EventId = 2, Level = LogLevel.Information, Message = "Goal of session {SessionId} blocked: {Rounds} round(s) of {MaxRounds} were spent")]
    public static partial void GoalBlocked(ILogger logger, string sessionId, int rounds, int maxRounds);

    [LoggerMessage(EventId = 3, Level = LogLevel.Warning, Message = "Goal write for session {SessionId} was refused: {Error}")]
    public static partial void RoundWriteRefused(ILogger logger, string sessionId, string error);

    [LoggerMessage(EventId = 4, Level = LogLevel.Warning, Message = "Goal block write for session {SessionId} was refused: {Error}")]
    public static partial void BlockWriteRefused(ILogger logger, string sessionId, string error);

    [LoggerMessage(EventId = 5, Level = LogLevel.Warning, Message = "Goal of session {SessionId} could not be reserved: another writer kept changing it")]
    public static partial void RoundGiveUp(ILogger logger, string sessionId);
}
