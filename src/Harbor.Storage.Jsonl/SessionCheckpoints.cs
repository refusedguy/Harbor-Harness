// SessionCheckpoints.cs — slice 1 of #1247 (PX1 checkpoints/rewind).
//
// A checkpoint is a named marker over a message index, persisted as a
// `"type":"checkpoint"` line in the session's JSONL file (jsonl backend
// only this slice; sqlite follows). Checkpoints are metadata, not history:
// the message read path skips them (see SessionFileReader.FoldRecord), so
// chat history, stats and export are byte-for-byte what they were before
// checkpoints existed.
//
// A rewind truncates the session to a checkpoint's anchor message via the
// existing DeleteMessagesAfterAsync ("rewind to here") and then appends a
// checkpoint line with RewindOf set — the honest trail: the dropped
// messages are gone, but the fact of the rewind (which checkpoint, how
// many removed, when) stays in the file instead of rewriting history
// silently. Checkpoints past the anchor are dropped by the same rewrite;
// checkpoints at or before it survive.

using System.Text.Json.Serialization;

namespace Harbor.Storage.Jsonl;

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

/// <summary>Outcome of <see cref="JsonlSessionStore.RewindToCheckpointAsync" />.</summary>
/// <param name="CheckpointId">The restored checkpoint's id.</param>
/// <param name="MessageId">Anchor message id kept as the new tail.</param>
/// <param name="Removed">Messages dropped after the anchor.</param>
/// <param name="Remaining">Messages in the session after the rewind.</param>
public sealed record SessionRewindOutcome(
    string CheckpointId,
    string MessageId,
    int Removed,
    int Remaining);

/// <summary>
///     On-disk shape of a checkpoint line. Written with <c>Type</c> always
///     <c>"checkpoint"</c> so the read path and the rewrite plans can tell
///     it apart from <c>"message"</c> and <c>"session"</c> lines by byte
///     probe, without decoding every record.
/// </summary>
internal sealed record CheckpointEntry(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("messageId")] string MessageId,
    [property: JsonPropertyName("messageIndex")] int MessageIndex,
    [property: JsonPropertyName("createdAt")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("label")] string? Label = null,
    [property: JsonPropertyName("rewindOf")] string? RewindOf = null,
    [property: JsonPropertyName("removed")] int Removed = 0);
