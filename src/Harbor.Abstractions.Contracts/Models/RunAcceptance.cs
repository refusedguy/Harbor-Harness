namespace Harbor.Abstractions.Models;

/// <summary>
///     Whether the user accepted the change a run produced (epic #41, slice B1.2).
/// </summary>
/// <remarks>
///     <para>
///         <b>The third independent axis of a run result.</b>
///         <see cref="RunStopReason" /> answers "how did the loop exit",
///         <see cref="RunVerification" /> answers "did the checks support the
///         work", and this answers "did the user want the work". No member can
///         be derived from either other axis: a clean exit does not imply a
///         passing check, a passing check does not imply acceptance, and a
///         rejection says nothing about whether the work itself was good. The
///         common state <c>{ Succeeded, NotVerified, Rejected }</c> is exactly
///         why the three live in separate members rather than one flat enum.
///     </para>
///     <para>
///         <b>Acceptance has a different temporal shape.</b> Execution is
///         sealed by the terminal event and verification is sealed at run end;
///         acceptance arrives after the run and may never arrive at all. A
///         persisted <see cref="RunOutcome" /> is therefore a point-in-time
///         view of this axis, not a reconstruction from the message history —
///         the store has no accept/reject message kind. A late decision
///         updates the value; the product does not re-derive it.
///     </para>
///     <para>
///         <b>Additive by construction.</b> This type was added after the
///         execution axis shipped. It does not replace, rename or re-number
///         <see cref="RunStopReason" />, and every caller written before it
///         keeps compiling: <see cref="RunOutcome.Acceptance" /> has an
///         explicit initializer so a caller with no acceptance channel reads
///         <see cref="NotOffered" />, never a plausible <see cref="Pending" />.
///     </para>
/// </remarks>
public enum RunAcceptance
{
    /// <summary>
    ///     A change was offered and the user has not decided yet. This is a
    ///     statement that a decision is expected, not that one was missed:
    ///     use <see cref="NotOffered" /> when there was no change to accept.
    /// </summary>
    Pending,

    /// <summary>
    ///     The user accepted the change that was offered. This says nothing
    ///     about <see cref="RunStopReason" /> or <see cref="RunVerification" />;
    ///     a user may accept work whose checks never ran.
    /// </summary>
    Accepted,

    /// <summary>
    ///     The user saw the change and did not want it. This is a verdict on
    ///     the change, not on the run: a rejected change can come from a run
    ///     that exited cleanly and passed every check. It is deliberately not
    ///     <see cref="RunStopReason.Failed" />.
    /// </summary>
    Rejected,

    /// <summary>
    ///     No change was offered for acceptance — for example an agent-only
    ///     run, or an outcome built by a caller that predates this axis and so
    ///     had no acceptance channel at all. Distinct from
    ///     <see cref="Rejected" /> (a decision was made) and from
    ///     <see cref="Pending" /> (a decision is expected).
    /// </summary>
    NotOffered,
}
