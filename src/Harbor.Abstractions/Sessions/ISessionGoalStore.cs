// ISessionGoalStore.cs — the optional goal surface of a session store (#1761).
//
// WHY A SEPARATE INTERFACE AND NOT A MEMBER OF ISessionStore
// -----------------------------------------------------------
// The same reasoning the checkpoint axis (#1247) and the feedback axis (#1746)
// already use: `ISessionStore` is the persistence contract every backend
// implements (memory included, and a backend that is not durable has no
// meaningful goal story), while the goal verbs are a seam only the DURABLE
// backends can honour. A host that wants goals names this interface once; a
// store that does not implement it is not forced into a member that would have
// to lie.
//
// THE SHAPE OF THE AXIS
// ---------------------
// The goal is APPEND-ONLY, like the rest of the store: every change appends a
// whole snapshot of the goal, and the effective goal is the newest one. The
// history of a goal's phases therefore survives, and the newest snapshot is
// the one a reader sees.
//
// The snapshot carries a REVISION, and that is the whole concurrency story:
// the two writers of a goal are the round driver (which counts rounds) and the
// agent (which completes or blocks the goal), and they can both be writing at
// the same moment. A writer appends revision N+1; a write that names an older
// revision is REFUSED rather than silently overwriting a newer fact. The
// refused writer re-reads and decides again — the loser of the race does not
// lose the fact it was trying to record, it records it against the newer
// revision.

namespace Harbor.Abstractions.Sessions;

/// <summary>
///     The optional goal surface of a session store (#1761): read the goal a
///     session is working toward, and append a new revision of it.
/// </summary>
/// <remarks>
///     <para>
///         <b>Failure contract (shared with <see cref="ISessionStore" />):</b>
///         expected states travel the <see cref="Result" /> rail — nothing here
///         throws except <see cref="OperationCanceledException" />. The shapes
///         are the shared ones: <c>Session '{id}' not found.</c> for a session
///         that is not there.
///     </para>
///     <para>
///         <b>Absence is not a failure.</b> A session with no goal is not an
///         error — it is a session that was never given one. The getter says
///         so with <see cref="Maybe{T}" />: the lookup can fail, the value can
///         be absent, and the two are told apart without a nullable payload
///         that could be either.
///     </para>
/// </remarks>
public interface ISessionGoalStore
{
    /// <summary>
    ///     Read the session's effective goal: the newest snapshot of it, if the
    ///     session has one.
    /// </summary>
    /// <param name="sessionId">The session id.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    ///     The goal, or <see cref="Maybe{T}.None" /> when the session has none.
    ///     Failure when the session does not exist or the store cannot answer.
    /// </returns>
    public Task<Result<Maybe<Goal>>> GetGoalAsync(
        string sessionId, CancellationToken ct = default);

    /// <summary>
    ///     Append a new revision of the session's goal.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The appended goal's <see cref="Goal.Revision" /> must be exactly
    ///         one past the revision already stored: the first goal of a
    ///         session is revision 1. A goal that names an older revision is
    ///         REFUSED — the store does not overwrite a newer fact with an
    ///         older one, because the two facts were computed against
    ///         different states of the world.
    ///     </para>
    ///     <para>
    ///         The whole goal is appended, not a delta: the store keeps every
    ///         revision, so the phases a goal went through stay in the log.
    ///     </para>
    /// </remarks>
    /// <param name="sessionId">The session id.</param>
    /// <param name="goal">The goal revision to append.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    ///     The appended goal, or failure when the session does not exist or
    ///     the revision is stale.
    /// </returns>
    public Task<Result<Goal>> AppendGoalAsync(
        string sessionId, Goal goal, CancellationToken ct = default);
}
