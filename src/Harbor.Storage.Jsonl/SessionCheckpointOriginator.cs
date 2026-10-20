// SessionCheckpointOriginator.cs — #1551 (Memento): the originator half of
// the checkpoint/rewind mechanics (#1247).
//
// Role boundary in this backend:
//
//   * ORIGINATOR = <see cref="JsonlSessionStore" /> — it owns the internal
//     state a capture/restore concerns (message order, the appended file
//     tail, the parse cache). Capture scans its own anchors and freezes
//     them into an opaque <see cref="SessionCheckpointMemento" />; restore
//     absorbs a memento and replays it (truncate + honest trail), never
//     re-deriving anything from the by-hand shapes.
//
//   * CARETAKER = the <c>ISessionCheckpointStore</c> surface (and the
//     callers behind it) — it resolves files, decides the session
//     existence policy, keeps and numbers the on-disk
//     <see cref="CheckpointEntry" /> rows, and knows a checkpoint only by
//     its opaque memento, never by index arithmetic.
//
//   * MEMENTO/kit = <see cref="SessionCheckpointMapperKit" /> —
//     the single conversion zone between the three shapes.
//
// Behaviour is byte-for-byte the pre-pattern flow (see
// tests/Harbor.Storage.Jsonl.Tests/JsonlCheckpointsTests.cs): same trail
// markers, same MessageNotFound/CheckpointNotFound failures.

using Harbor.Storage.Shared;

namespace Harbor.Storage.Jsonl;

// The base list is the store's WHOLE set of seams, and it is repeated here
// verbatim because a partial declaration may not name a different one:
// ISessionFeedbackStore (#1746) joined it in JsonlSessionStore.cs, and a
// partial type whose parts disagree about their base interfaces does not
// compile.
public sealed partial class JsonlSessionStore : ISessionStore, ISessionCheckpointStore, ISessionFeedbackStore
{
    /// <summary>
    ///     Originator capture (<b>#1551 Memento</b>): find the anchor in this
    ///     store's own message order, freeze id/index/label/time into an
    ///     opaque <see cref="SessionCheckpointMemento" />, project it onto the
    ///     on-disk row the caretaker appends and onto the contract row the
    ///     caller receives — via the mapper kit, nowhere else.
    /// </summary>
    /// <remarks>
    ///     Called only after the caretaker (<see cref="CreateCheckpointAsync" />)
    ///     observed cancellation, resolved the file and settled the session
    ///     existence policy. No locking here that the append does not already
    ///     provide.
    /// </remarks>
    /// <param name="sessionId">The session id.</param>
    /// <param name="sessionFile">Resolved session file path.</param>
    /// <param name="messageId">Anchor message id to mark.</param>
    /// <param name="label">Optional human label.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The recorded checkpoint, or failure if the message is not found.</returns>
    internal async Task<Result<SessionCheckpoint>> CaptureCheckpointAsync(
        string sessionId, string sessionFile, string messageId, string? label, CancellationToken ct)
    {
        var messagesResult = await GetMessagesAsync(sessionId, ct).ConfigureAwait(false);
        if (messagesResult.IsFailure)
            return messagesResult.ConvertFailure<SessionCheckpoint>();

#pragma warning disable CFE0001
        // CFE0001: false positive — the IsFailure guard above is an early
        // return, a control-flow shape the analyzer does not model.
        IReadOnlyList<AgentMessage> messages = messagesResult.Value;
#pragma warning restore CFE0001

        // The anchor scan is originator work (#1551): the caretaker may not
        // even know message order exists. Plain for-loop — hot-ish path.
        int index = -1;
        for (int i = 0; i < messages.Count; i++)
        {
            if (string.Equals(messages[i].Id, messageId, StringComparison.Ordinal))
            {
                index = i;
                break;
            }
        }

        if (index < 0)
            return Result.Failure<SessionCheckpoint>(SessionStoreErrors.MessageNotFound(sessionId, messageId));

        // #1551: the originator alone mints the snapshot; the session-file
        // row and the returned contract row are its two projections.
        var memento = new SessionCheckpointMemento(
            Guid.NewGuid().ToString("N"), messageId, index, DateTimeOffset.UtcNow, label);

        var appended = await _checkpoints.AppendCheckpointEntryAsync(
                sessionId, sessionFile, SessionCheckpointMapperKit.ToRow(memento), ct)
            .ConfigureAwait(false);
        if (appended.IsFailure)
            return appended.ConvertFailure<SessionCheckpoint>();

        return Result.Success(SessionCheckpointMapperKit.ToContract(memento, sessionId));
    }

    /// <summary>
    ///     Originator restore (<b>#1551 Memento</b>): materialize the opaque
    ///     restore state for <paramref name="checkpointId" /> from the
    ///     session's listed rows through the mapper kit, then replay it —
    ///     truncate to the memento's anchor (the caretaker's id-matched rows
    ///     past it go with the dropped future) and append the honest-trail
    ///     line built from the same memento. No index arithmetic, no manual
    ///     trail shape.
    /// </summary>
    /// <remarks>
    ///     Trail rows are skipped by the id match — a rewind of a rewind is
    ///     not a checkpoint state. Behaviour parity: same trail marker
    ///     (fresh id, anchor kept, <c>RewindOf</c> set, dropped count kept),
    ///     same <see cref="SessionStoreErrors.CheckpointNotFound" /> failure.
    /// </remarks>
    /// <param name="sessionId">The session id.</param>
    /// <param name="sessionFile">Resolved session file path.</param>
    /// <param name="checkpointId">Id of the checkpoint to restore.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>How many messages were dropped and how many remain.</returns>
    internal async Task<Result<SessionRewindOutcome>> RestoreCheckpointAsync(
        string sessionId, string sessionFile, string checkpointId, CancellationToken ct)
    {
        var listed = await ListCheckpointsAsync(sessionId, ct).ConfigureAwait(false);
        if (listed.IsFailure)
            return listed.ConvertFailure<SessionRewindOutcome>();

#pragma warning disable CFE0001
        // CFE0001: false positive — the IsFailure guard above is an early return.
        IReadOnlyList<SessionCheckpoint> checkpoints = listed.Value;
#pragma warning restore CFE0001

        SessionCheckpointMemento? memento = null;
        foreach (var c in checkpoints)
        {
            if (!c.IsRewindTrail && string.Equals(c.Id, checkpointId, StringComparison.Ordinal))
            {
                memento = SessionCheckpointMapperKit.ToMemento(c);
                break;
            }
        }

        if (memento is null)
            return Result.Failure<SessionRewindOutcome>(SessionStoreErrors.CheckpointNotFound(sessionId, checkpointId));

        var truncated = await DeleteMessagesAfterAsync(sessionId, memento.MessageId, ct).ConfigureAwait(false);
        if (truncated.IsFailure)
            return truncated.ConvertFailure<SessionRewindOutcome>();

#pragma warning disable CFE0001
        // CFE0001: false positive — the IsFailure guard above is an early return.
        int removed = truncated.Value;
#pragma warning restore CFE0001

        var trailed = await _checkpoints.AppendCheckpointEntryAsync(
                sessionId, sessionFile,
                SessionCheckpointMapperKit.ToTrailRow(memento, removed, DateTimeOffset.UtcNow), ct)
            .ConfigureAwait(false);
        if (trailed.IsFailure)
            return trailed.ConvertFailure<SessionRewindOutcome>();

        var reread = await GetMessagesAsync(sessionId, ct).ConfigureAwait(false);
        if (reread.IsFailure)
            return reread.ConvertFailure<SessionRewindOutcome>();

#pragma warning disable CFE0001
        // CFE0001: false positive — the IsFailure guard above is an early return.
        int remaining = reread.Value.Count;
#pragma warning restore CFE0001

        return Result.Success(
            new SessionRewindOutcome(memento.Id, memento.MessageId, removed, remaining));
    }
}
