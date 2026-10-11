using CSharpFunctionalExtensions;

namespace Harbor.Abstractions.Sessions;

/// <summary>
///     Optional checkpoint surface of a session store (#1247): mark a
///     message, list the marks, and rewind the session to one.
/// </summary>
/// <remarks>
///     <para>
///         Interface segregation: <see cref="ISessionStore" /> stays the
///         persistence contract every backend implements (Memory included,
///         and a backend that is not durable has no meaningful checkpoint
///         story), while the checkpoint verbs are a separate seam the two
///         durable backends — JSONL and SQLite — implement. A host that
///         wants checkpoints names this interface once; a store that does
///         not implement it is not forced into throwing members (LSP).
///     </para>
///     <para>
///         <b>Failure contract (shared with <see cref="ISessionStore" />):</b>
///         expected states travel the <see cref="Result" /> rail — nothing
///         here throws except <see cref="OperationCanceledException" />.
///         Shapes are the shared ones: <c>Session '{id}' not found.</c>,
///         <c>Message '{mid}' not found in session '{sid}'.</c> and
///         <c>Checkpoint '{cid}' not found in session '{sid}'.</c>
///     </para>
///     <para>
///         A rewind truncates history but records itself: after the
///         truncation the store appends a trail marker
///         (<see cref="SessionCheckpoint.RewindOf" /> set), so the dropped
///         messages are gone while the fact of the rewind survives.
///     </para>
/// </remarks>
public interface ISessionCheckpointStore
{
    /// <summary>
    ///     Mark a checkpoint over <paramref name="messageId" />: the anchor's
    ///     zero-based index in message order is recorded, so a later
    ///     <see cref="RewindToCheckpointAsync" /> can restore it.
    /// </summary>
    /// <param name="sessionId">The session id.</param>
    /// <param name="messageId">Anchor message id to mark.</param>
    /// <param name="label">Optional human label.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The recorded checkpoint, or failure if session/message not found.</returns>
    public Task<Result<SessionCheckpoint>> CreateCheckpointAsync(
        string sessionId, string messageId, string? label = null, CancellationToken ct = default);

    /// <summary>
    ///     List the session's checkpoints and rewind trail markers in
    ///     recording order.
    /// </summary>
    /// <param name="sessionId">The session id.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The checkpoints, or failure if the session does not exist.</returns>
    public Task<Result<IReadOnlyList<SessionCheckpoint>>> ListCheckpointsAsync(
        string sessionId, CancellationToken ct = default);

    /// <summary>
    ///     Rewind the session to <paramref name="checkpointId" />: every
    ///     message after the anchor is dropped (the anchor itself is kept),
    ///     checkpoints anchored past it go with the dropped future, and a
    ///     trail marker records the rewind.
    /// </summary>
    /// <param name="sessionId">The session id.</param>
    /// <param name="checkpointId">Id of the checkpoint to restore.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>How many messages were dropped and how many remain.</returns>
    public Task<Result<SessionRewindOutcome>> RewindToCheckpointAsync(
        string sessionId, string checkpointId, CancellationToken ct = default);
}
