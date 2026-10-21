// SessionCompactionRows.cs — the on-disk shape of the compaction records
// (#1767) and the mapper between them and the contract.
//
// The compaction record lives in the session file as its OWN record, beside
// the messages, the checkpoints and the feedback:
//
//   {"type":"compaction","kind":"start","turn":3,"error":null,
//    "pruned":0,"tokensSaved":0,"occurredAt":"…","orphanedTurn":null}
//   {"type":"compaction","kind":"end","turn":3,"error":null,
//    "pruned":41,"tokensSaved":8123,"occurredAt":"…","orphanedTurn":null}
//
// Why its own records and not fields on a message: a compaction record is
// what happened to the SESSION, not what the model said. Keeping it out of
// the message records is what makes "the compaction never reaches the model"
// a property of the format rather than a promise — the message read path
// drops these lines the way it drops the checkpoint markers.
//
// The kind is stored as its NAME, not as a number, for the same reason the
// feedback axis stores its ratings as names: a session file is read by people
// as often as by code, and a vocabulary that grows must not renumber the
// records already written. A name this build does not know is a record it
// refuses rather than one it guesses at.

using System.Text.Json.Serialization;

namespace Harbor.Storage.Jsonl;

/// <summary>
///     The <c>"type":"compaction"</c> line: one record of a compaction
///     operation — the lock being taken, or released.
/// </summary>
internal sealed record SessionCompactionEntry(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("turn")] int? Turn,
    [property: JsonPropertyName("error")] string? Error,
    [property: JsonPropertyName("pruned")] int Pruned,
    [property: JsonPropertyName("tokensSaved")] int TokensSaved,
    [property: JsonPropertyName("occurredAt")] DateTimeOffset OccurredAt,
    [property: JsonPropertyName("orphanedTurn")] int? OrphanedTurn = null);

/// <summary>
///     The one mapper between the compaction lines and the contract records —
///     the same "one border, one converter" rule the checkpoint and feedback
///     axes follow.
/// </summary>
internal static class SessionCompactionMapperKit
{
    /// <summary>
    ///     Parse a stored kind name. A name this build does not know is a
    ///     REFUSAL, not a default: a record from a newer build is skipped
    ///     with a warning rather than read as an "end" that never happened.
    /// </summary>
    /// <param name="value">The stored name.</param>
    /// <param name="kind">The parsed kind.</param>
    /// <returns>True when the name is one this build knows.</returns>
    public static bool TryParseKind(string? value, out SessionCompactionEventKind kind)
    {
        // Enum.TryParse also accepts the NUMERIC value of a member, so a
        // hand-edited "7" would parse into a value this build does not
        // define. IsDefined is the second half of the refusal.
        if (!Enum.TryParse(value, ignoreCase: true, out kind))
        {
            return false;
        }

        return Enum.IsDefined(kind);
    }

    /// <summary>
    ///     Map a scanned compaction line to the contract record.
    /// </summary>
    /// <param name="row">The decoded line.</param>
    /// <param name="kind">The parsed kind.</param>
    /// <param name="sequence">The record's position among the session's compaction records.</param>
    /// <returns>The contract record.</returns>
    public static SessionCompactionEvent ToContract(
        SessionCompactionEntry row,
        SessionCompactionEventKind kind,
        int sequence) =>
        new()
        {
            Kind = kind,
            Turn = row.Turn,
            Error = row.Error,
            PrunedMessageCount = row.Pruned,
            TokensSaved = row.TokensSaved,
            OccurredAt = row.OccurredAt,
            Sequence = sequence,
            OrphanedTurn = row.OrphanedTurn,
        };

    /// <summary>
    ///     Map a contract record to the line shape the store appends.
    /// </summary>
    /// <param name="compactionEvent">The contract record.</param>
    /// <returns>The on-disk line shape.</returns>
    public static SessionCompactionEntry ToRow(SessionCompactionEvent compactionEvent) =>
        new(
            "compaction",
            compactionEvent.Kind.ToString(),
            compactionEvent.Turn,
            compactionEvent.Error,
            compactionEvent.PrunedMessageCount,
            compactionEvent.TokensSaved,
            compactionEvent.OccurredAt,
            compactionEvent.OrphanedTurn);
}
