// SessionCheckpoints.cs — the checkpoint/rewind model of #1247, shared by
// every durable session-store backend.
//
// Slice 1 (#1253) introduced these records inside Harbor.Storage.Jsonl and
// implemented the verbs for the JSONL backend only. Slice 2 moves the model
// to Domain so the SQLite backend can speak the same contract without an
// Infrastructure→Infrastructure reference (the matrix forbids it, and the
// JSONL on-disk "checkpoint" line shape is not a portable concept anyway).
//
// A checkpoint is a named marker over a message index. A rewind truncates
// the session to a checkpoint's anchor message and then records a trail
// marker — the honest trail: the dropped messages are gone, but the fact of
// the rewind (which checkpoint, how many removed, when) stays recorded
// instead of rewriting history silently.

namespace Harbor.Abstractions.Sessions;

/// <summary>
///     A named marker over a session message, plus the honest-trail record
///     of a rewind. <see cref="RewindOf" /> is null for a plain checkpoint;
///     a rewind trail marker sets it to the restored checkpoint's id and
///     <see cref="Removed" /> to the number of messages the rewind dropped.
/// </summary>
/// <param name="Id">Checkpoint (or trail marker) id.</param>
/// <param name="SessionId">Owning session.</param>
/// <param name="MessageId">Anchor message id — the new tail after a rewind to this checkpoint.</param>
/// <param name="MessageIndex">Zero-based index of the anchor in message order when the checkpoint was taken.</param>
/// <param name="CreatedAt">When the checkpoint (or rewind) was recorded.</param>
/// <param name="Label">Optional human label.</param>
/// <param name="RewindOf">Restored checkpoint id for trail markers; null for plain checkpoints.</param>
/// <param name="Removed">Messages dropped by the rewind; 0 for plain checkpoints.</param>
public sealed record SessionCheckpoint(
    string Id,
    string SessionId,
    string MessageId,
    int MessageIndex,
    DateTimeOffset CreatedAt,
    string? Label,
    string? RewindOf = null,
    int Removed = 0)
{
    /// <summary>True for honest-trail rewind markers as opposed to plain checkpoints.</summary>
    public bool IsRewindTrail => RewindOf is not null;
}

/// <summary>Outcome of <see cref="ISessionCheckpointStore.RewindToCheckpointAsync" />.</summary>
/// <param name="CheckpointId">The restored checkpoint's id.</param>
/// <param name="MessageId">Anchor message id kept as the new tail.</param>
/// <param name="Removed">Messages dropped after the anchor.</param>
/// <param name="Remaining">Messages in the session after the rewind.</param>
public sealed record SessionRewindOutcome(
    string CheckpointId,
    string MessageId,
    int Removed,
    int Remaining);
