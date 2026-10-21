// ICompactionEventStore.cs — the durable compaction-record seam of a session
// store (#1767).
//
// WHY A SEPARATE SEAM
// -------------------
// Interface segregation, the same call the checkpoint axis made (#1247) and
// the feedback axis made (#1746): ISessionStore stays the persistence
// contract every backend implements, while the two verbs below are a seam a
// durable backend implements when it can record compaction events. A store
// that does not implement it is not forced into members it cannot honour
// (LSP), and the caller can ASK whether the seam is there before it depends
// on it.
//
// WHY THE COMPACTION DEPENDS ON IT
// --------------------------------
// A compaction writes to the session log, and the rule is that it may not
// write without the lock marker being in the log first. A store that records
// no compaction events has no marker vocabulary at all, so the caller asks
// for the seam first: with the seam, the compaction is bracketed by
// `compaction/start` and `compaction/end`; without it, the in-process guard
// is the only exclusion and the runtime says so out loud.
//
// THE ORDER OF THE TWO RECORDS IS THE CONTRACT
// ---------------------------------------------
// `AppendCompactionEventAsync` is called with a start record BEFORE the
// summarizer runs, and with an end record AFTER everything the compaction
// wrote. A caller that appends the end first would lose the only free signal
// a crash leaves behind: an unmatched start.

namespace Harbor.Abstractions.Sessions;

/// <summary>
///     The compaction-record seam of a session store: append one durable
///     compaction record, and read the session's records back (#1767).
/// </summary>
/// <remarks>
///     <para>
///         The records are log-only: they never enter the model context. They
///         are the lock and the audit trail of the compaction operation — the
///         start that takes the lock, the end that releases it, and the owner
///         (the turn) that says which compaction held it.
///     </para>
///     <para>
///         The failure contract is the shared one: expected states travel the
///         <see cref="Result" /> rail, and nothing here throws except
///         <see cref="OperationCanceledException" />.
///     </para>
/// </remarks>
public interface ICompactionEventStore
{
    /// <summary>
    ///     Append one compaction record to the session's log.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The record's <see cref="SessionCompactionEvent.Sequence" /> is
    ///         assigned by the store: it is the record's position among the
    ///         session's compaction records, counted from zero in write order.
    ///         A caller that sets it is overwritten.
    ///     </para>
    ///     <para>
    ///         The append is ordered against the session's other writes: a
    ///         record appended while a compaction is in flight lands INSIDE
    ///         the compaction's bracket, between its start and its end.
    ///     </para>
    /// </remarks>
    /// <param name="sessionId">The session id.</param>
    /// <param name="compactionEvent">The record to append.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Success, or failure if the session does not exist.</returns>
    public Task<Result> AppendCompactionEventAsync(
        string sessionId,
        SessionCompactionEvent compactionEvent,
        CancellationToken ct = default);

    /// <summary>
    ///     Read the session's compaction records, in write order.
    /// </summary>
    /// <remarks>
    ///     A session that never compacted yields an empty list, not a failure:
    ///     "no compaction happened" is a fact, not an error.
    /// </remarks>
    /// <param name="sessionId">The session id.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    ///     The records in write order, or failure if the session does not
    ///     exist.
    /// </returns>
    public Task<Result<IReadOnlyList<SessionCompactionEvent>>> GetCompactionEventsAsync(
        string sessionId,
        CancellationToken ct = default);
}
