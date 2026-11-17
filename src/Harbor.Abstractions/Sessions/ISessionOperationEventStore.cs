// ISessionOperationEventStore.cs — the durable crash-marker seam of a session
// store (#2226).
//
// WHY A SEPARATE SEAM
// -------------------
// Interface segregation, the same call the checkpoint axis (#1247) and the
// compaction axis (#1767) made: ISessionStore stays the persistence contract
// every backend implements, while these two verbs are a seam a durable backend
// implements when it can record operation markers. A store that does not
// implement it is not forced into members it cannot honour (LSP), and the
// caller asks for the seam before it depends on it.
//
// THE ORDER OF THE TWO RECORDS IS THE CONTRACT
// --------------------------------------------
// AppendOperationEventAsync is called with a start marker BEFORE the operation
// writes anything else, and with an end marker AFTER everything it wrote. The
// end is written last so a crash leaves an unmatched start — the only free
// signal a crash leaves behind.

namespace Harbor.Abstractions.Sessions;

/// <summary>
///     The operation-record seam of a session store: append one durable
///     fork/turn marker, and read the session's markers back (#2226).
/// </summary>
/// <remarks>
///     The records are log-only: they never enter the model context. The
///     failure contract is the shared one — expected states travel the
///     <see cref="Result" /> rail, and nothing throws except
///     <see cref="OperationCanceledException" />.
/// </remarks>
public interface ISessionOperationEventStore
{
    /// <summary>
    ///     Append one operation marker to the session's log.
    /// </summary>
    /// <remarks>
    ///     The record's <see cref="SessionOperationEvent.Sequence" /> is
    ///     assigned by the store; a caller that sets it is overwritten. The
    ///     append is ordered against the session's other writes by the same
    ///     lock the message appends use.
    /// </remarks>
    /// <param name="sessionId">The session id.</param>
    /// <param name="operationEvent">The marker to append.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Success, or failure if the session does not exist.</returns>
    public Task<Result> AppendOperationEventAsync(
        string sessionId,
        SessionOperationEvent operationEvent,
        CancellationToken ct = default);

    /// <summary>
    ///     Read the session's operation markers, in write order.
    /// </summary>
    /// <remarks>
    ///     A session that never forked or ran a turn yields an empty list, not
    ///     a failure: "no markers" is a fact, not an error.
    /// </remarks>
    /// <param name="sessionId">The session id.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The markers in write order, or failure if the session does not exist.</returns>
    public Task<Result<IReadOnlyList<SessionOperationEvent>>> GetOperationEventsAsync(
        string sessionId,
        CancellationToken ct = default);
}
