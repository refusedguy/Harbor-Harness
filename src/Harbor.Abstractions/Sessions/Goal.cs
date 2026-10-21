// Goal.cs — the durable goal of #1761.
//
// A GOAL is the subject of a run: what the agent is working toward, and where
// that work stands. It is DURABLE (it lives in the session log, so it survives
// a restart) and it is PHASED: active, blocked, or complete.
//
// WHY A PHASE AND NOT A FLAG
// --------------------------
// The agent loop already knows how many turns it took (`AgentDefinition.MaxSteps`)
// and how much it spent (`RunBudget`). Neither of those says whether the WORK
// was finished: a run that ends on the step cap and a run that ends because the
// agent is done look identical in the transcript. A goal adds the missing
// question — "is this done?" — and answers it in a shape a caller can act on:
// `complete` can be accepted, `blocked` can be escalated, and the two are not
// the same fact.
//
// WHY THE PHASE IS DURABLE AND THE ACTIVATION IS NOT
// ---------------------------------------------------
// The PHASE (this file) is a fact about the work and lives in the session log.
// Whether a driver is currently RUNNING the goal is a fact about THIS process
// and lives nowhere durable. The split is deliberate: "the process restarted"
// must not read as "the goal was cancelled". A goal that was active when the
// process died is still active when the next process opens the session; what
// the next process does with it is the activation's business.
//
// WHAT THIS IS NOT
// ----------------
// This is not a second run loop. The goal rides the EXISTING turn loop: the
// agent's own turn boundary is where a round is reserved and where the round
// prompt is injected. The run-limits contour (#41: `RunLimitKind`,
// `RunOutcome`) is what reports the end of a run, and a goal that runs out of
// rounds ends the run with `RunLimitKind.MaxGoalRounds` — the same shape as
// every other limit, not a parallel mechanism.

namespace Harbor.Abstractions.Sessions;

/// <summary>
///     Where a goal stands (#1761).
/// </summary>
/// <remarks>
///     <para>
///         Three members, and the third one is the point. <see cref="Active" />
///         is the only phase in which work continues;
///         <see cref="Complete" /> and <see cref="Blocked" /> are both terminal
///         and both say something the caller can act on.
///     </para>
///     <para>
///         <b>Blocked is not failed.</b> A blocked goal is one that cannot
///         proceed right now — the round budget is spent, or the agent reported
///         that something is missing. The work is INCOMPLETE, which is exactly
///         what separates it from <see cref="Complete" />. A goal that could
///         only say "done" or "not done" would force the caller to guess, and
///         the guess is what this axis exists to remove.
///     </para>
/// </remarks>
public enum GoalPhase
{
    /// <summary>
    ///     The goal is being worked on. The only phase in which a round may be
    ///     reserved for it.
    /// </summary>
    Active,

    /// <summary>
    ///     The goal cannot proceed. <see cref="Goal.BlockedReason" /> says why
    ///     and <see cref="Goal.BlockedCode" /> says which kind of blockage it
    ///     is. Terminal.
    /// </summary>
    Blocked,

    /// <summary>
    ///     The goal was reached. Terminal.
    /// </summary>
    Complete,
}

/// <summary>
///     Which kind of blockage stopped a goal (#1761).
/// </summary>
/// <remarks>
///     A code, not a sentence: the reason text is free-form and is written for
///     a human, while the code is what a caller branches on. Two kinds are
///     enough for the slice, and both are distinguishable WITHOUT reading the
///     agent's prose — which is the property that matters (see ADR-020: the
///     agent's narration is a claim, not evidence).
/// </remarks>
public enum GoalBlockCode
{
    /// <summary>
    ///     The agent reported that it cannot proceed, and said why. The agent
    ///     asked for it, so the reason is its own.
    /// </summary>
    AgentReported,

    /// <summary>
    ///     The goal's round budget was spent. Nothing malfunctioned and nobody
    ///     pressed stop: the work is incomplete because the bound the goal was
    ///     given ran out.
    /// </summary>
    RoundLimit,
}

/// <summary>
///     A durable goal: the subject of a session's work, and where that work
///     stands (#1761).
/// </summary>
/// <param name="Objective">
///     What the goal is. The text the agent is working toward, and the text a
///     round prompt carries.
/// </param>
/// <param name="Phase">
///     Where the goal stands. Only <see cref="GoalPhase.Active" /> allows more
///     work.
/// </param>
/// <param name="MaxRounds">
///     How many autonomous rounds the goal may get. The bound the goal was
///     given; reaching it blocks the goal with
///     <see cref="GoalBlockCode.RoundLimit" />.
/// </param>
/// <param name="RoundsStarted">
///     How many autonomous rounds the driver has already started for this goal.
///     The user's opening prompt is NOT one of them: it is the request that put
///     the goal in motion, not a round the driver reserved.
/// </param>
/// <param name="Revision">
///     The goal's revision counter. It is the optimistic-concurrency token of
///     the goal axis: a writer appends revision N+1, and a write that names an
///     older revision is refused rather than silently overwriting a newer
///     fact (the two writers of a goal are the driver and the agent).
/// </param>
/// <param name="CreatedAt">When the goal was set.</param>
/// <param name="UpdatedAt">When the goal last changed phase or counter.</param>
/// <param name="BlockedReason">
///     Why the goal is blocked, in the words of whoever blocked it. Null while
///     the goal is not blocked.
/// </param>
/// <param name="BlockedCode">
///     Which kind of blockage this is. Null while the goal is not blocked.
/// </param>
/// <remarks>
///     <para>
///         <b>One goal per session (slice scope).</b> The goal is identified by
///         the session that holds it, so this record carries no id of its own.
///         A session with several goals needs an id and a selector, and both
///         are follow-up work — not a field to guess at now.
///     </para>
///     <para>
///         <b>The record is a snapshot, and the store keeps every one.</b> The
///         store appends a whole goal per change, so the history of a goal's
///         phases survives; the effective goal is the newest snapshot. That is
///         the same append-only shape the session log itself uses (#1784), and
///         it is why <see cref="Revision" /> is a counter rather than a
///         timestamp: two writers can be told apart by it without a clock.
///     </para>
/// </remarks>
public sealed record Goal(
    string Objective,
    GoalPhase Phase,
    int MaxRounds,
    int RoundsStarted,
    int Revision,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string? BlockedReason = null,
    GoalBlockCode? BlockedCode = null);
