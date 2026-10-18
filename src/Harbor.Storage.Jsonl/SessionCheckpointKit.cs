// SessionCheckpointKit.cs — #1551 (Memento): the single mapping zone for
// #1247's checkpoint mechanics.
//
// Three shapes meet at one border and must never be converted ad hoc again:
//
//   * <see cref="CheckpointEntry" />   — the on-disk "type":"checkpoint"
//     line (SessionCheckpoints.cs). Caretaker storage: appended, kept,
//     numbered — never interpreted.
//   * <see cref="SessionCheckpoint" /> — the backend-agnostic contract row
//     (Harbor.Abstractions.Sessions) handed out over
//     <see cref="ISessionCheckpointStore" />.
//   * <see cref="SessionCheckpointMemento" /> — the originator's opaque
//     snapshot of one plain checkpoint, created when the originator freezes
//     its anchor state and absorbed only by the originator's restore.
//
// Every conversion is a member of <see cref="SessionCheckpointMapperKit" />;
// no type here builds another by hand. The kit is deliberately dumb — pure
// functions, no I/O, no locks — so the pattern's boundary (the first
// comment's border) is the only place the three shapes ever touch.

namespace Harbor.Storage.Jsonl;

/// <summary>
///     Originator-side opaque snapshot of a plain checkpoint
///     (<b>#1551 Memento</b>). Carries exactly the state a later rewind
///     restores by: the checkpoint's id, its anchor message and index, plus
///     the frozen creation time and label of the capture. Trail facts
///     (<c>RewindOf</c> / <c>Removed</c>) are deliberately absent — a
///     memento is a capture, not a rewind's residue.
/// </summary>
/// <param name="Id">Checkpoint id, minted by the originator at capture.</param>
/// <param name="MessageId">Anchor message id — the new tail after a restore.</param>
/// <param name="MessageIndex">Zero-based anchor index in message order at capture time.</param>
/// <param name="CreatedAt">When the capture was frozen.</param>
/// <param name="Label">Optional human label from the capture call.</param>
internal sealed record SessionCheckpointMemento(
    string Id,
    string MessageId,
    int MessageIndex,
    DateTimeOffset CreatedAt,
    string? Label);

/// <summary>
///     The one mapping zone of the checkpoint pattern (<b>#1551</b>):
///     <see cref="CheckpointEntry" /> rows ↔ public
///     <see cref="SessionCheckpoint" /> rows ↔ the originator's opaque
///     <see cref="SessionCheckpointMemento" />, plus the honest-trail line a
///     restore appends. Pure — no I/O, no locks, no id or timestamp minting.
/// </summary>
internal static class SessionCheckpointMapperKit
{
    /// <summary>
    ///     Map an on-disk scan row to the public contract row for
    ///     <paramref name="sessionId" />. Trail facts ride through as-is, so
    ///     listing cannot lose the honest trail.
    /// </summary>
    /// <param name="row">Decoded <c>"type":"checkpoint"</c> line.</param>
    /// <param name="sessionId">Owning session id.</param>
    /// <returns>The contract row the interface hands out.</returns>
    public static SessionCheckpoint ToContract(CheckpointEntry row, string sessionId) =>
        new(row.Id, sessionId, row.MessageId, row.MessageIndex,
            row.CreatedAt, row.Label, row.RewindOf, row.Removed);

    /// <summary>
    ///     Map the originator's fresh capture to the public contract row for
    ///     <paramref name="sessionId" /> — the id/index/label the caller of
    ///     <c>CreateCheckpointAsync</c> receives.
    /// </summary>
    /// <param name="memento">The opaque capture snapshot.</param>
    /// <param name="sessionId">Owning session id.</param>
    /// <returns>The contract row for the capture.</returns>
    public static SessionCheckpoint ToContract(SessionCheckpointMemento memento, string sessionId) =>
        new(memento.Id, sessionId, memento.MessageId, memento.MessageIndex,
            memento.CreatedAt, memento.Label);

    /// <summary>
    ///     Map the originator's fresh capture to the persisted on-disk line —
    ///     the caretaker appends it byte-for-byte and could never reconstruct
    ///     it (mementos do not travel out of the originator as rows).
    /// </summary>
    /// <param name="memento">The opaque capture snapshot.</param>
    /// <returns>The on-disk <c>"type":"checkpoint"</c> line shape.</returns>
    public static CheckpointEntry ToRow(SessionCheckpointMemento memento) =>
        new("checkpoint", memento.Id, memento.MessageId, memento.MessageIndex,
            memento.CreatedAt, memento.Label);

    /// <summary>
    ///     Materialize the opaque restore state from a listed contract row.
    ///     Trail rows are not captures — a rewind of a rewind is not a
    ///     checkpoint state the originator restores — so they must not be
    ///     handed to this member; the originator's id match already skips them.
    /// </summary>
    /// <param name="checkpoint">A plain (non-trail) listed checkpoint row.</param>
    /// <returns>The opaque snapshot a restore absorbs.</returns>
    public static SessionCheckpointMemento ToMemento(SessionCheckpoint checkpoint) =>
        new(checkpoint.Id, checkpoint.MessageId, checkpoint.MessageIndex,
            checkpoint.CreatedAt, checkpoint.Label);

    /// <summary>
    ///     Map a restore to its honest-trail line: a fresh trail id, the
    ///     memento's anchor kept as tail, <c>RewindOf</c> pointing at the
    ///     restored checkpoint and the dropped count carried in
    ///     <paramref name="removed" /> — recorded, not silent.
    /// </summary>
    /// <param name="memento">The snapshot being restored.</param>
    /// <param name="removed">Messages the rewind dropped.</param>
    /// <param name="now">Trail timestamp, minted by the originator at restore time.</param>
    /// <returns>The on-disk trail line shape.</returns>
    public static CheckpointEntry ToTrailRow(SessionCheckpointMemento memento, int removed, DateTimeOffset now) =>
        new("checkpoint", Guid.NewGuid().ToString("N"),
            memento.MessageId, memento.MessageIndex, now, null, memento.Id, removed);
}
