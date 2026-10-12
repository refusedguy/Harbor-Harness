// SessionCheckpoints.cs — on-disk half of #1247 (PX1 checkpoints/rewind).
//
// A checkpoint is a named marker over a message index, persisted as a
// `"type":"checkpoint"` line in the session's JSONL file. Checkpoints are
// metadata, not history: the message read path skips them (see
// SessionFileReader.FoldRecord), so chat history, stats and export are
// byte-for-byte what they were before checkpoints existed.
//
// A rewind truncates the session to a checkpoint's anchor message via the
// existing DeleteMessagesAfterAsync ("rewind to here") and then appends a
// checkpoint line with RewindOf set — the honest trail: the dropped
// messages are gone, but the fact of the rewind (which checkpoint, how
// many removed, when) stays in the file instead of rewriting history
// silently. Checkpoints past the anchor are dropped by the same rewrite;
// checkpoints at or before it survive.
//
// The public model (SessionCheckpoint / SessionRewindOutcome) moved to
// Harbor.Abstractions.Sessions in slice 2 so the SQLite backend implements
// the same contract; only this backend's line shape stays here.

using System.Text.Json.Serialization;

namespace Harbor.Storage.Jsonl;

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
