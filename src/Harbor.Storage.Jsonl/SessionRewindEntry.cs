// SessionRewindEntry.cs — the on-disk half of the append-only rewind (#1752).
//
// A rewind used to REWRITE the session file: the tail after the anchor was
// dropped and the file was replaced with what was left. That is the one thing
// an append-only log must never do — the text the agent produced after the
// anchor stopped existing, and "what did the agent actually say?" had no
// answer left to give.
//
// A rewind is now a RECORD, appended to the file like any other:
//
//   {"type":"rewind","id":"r1","anchorId":"m2","at":"2026-10-20T...Z","removed":3}
//
// The records the rewind cut out stay in the file. The surface is what the
// record says: the messages between the anchor and the event left it, and
// everything after the event is untouched (so "rewind and carry on" still
// works). The type is self-contained: a file written before this record
// existed cannot contain one, which is why the reader needs no format bump to
// understand it.
//
// The legacy trail marker — a "type":"checkpoint" line with rewindOf set —
// stays what it always was: metadata about a restore, not a surface event.
// In a legacy file the tail was physically gone, so a trail marker there
// hides nothing.

using System.Text.Json.Serialization;

namespace Harbor.Storage.Jsonl;

/// <summary>
///     On-disk shape of a rewind event: the record that says "the messages
///     after this anchor left the surface" WITHOUT the messages being removed
///     from the file.
/// </summary>
/// <param name="Type">Always <c>"rewind"</c>, so the read path can tell the record apart.</param>
/// <param name="Id">The event's own id.</param>
/// <param name="AnchorId">The id of the message the tail was cut at. The anchor stays.</param>
/// <param name="At">When the rewind happened.</param>
/// <param name="Removed">How many messages left the surface.</param>
internal sealed record SessionRewindEntry(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("anchorId")] string AnchorId,
    [property: JsonPropertyName("at")] DateTimeOffset At,
    [property: JsonPropertyName("removed")] int Removed);
