// SessionOperationRows.cs — the on-disk shape of the fork/turn crash markers
// (#2226) and the mapper between them and the contract.
//
// An operation marker lives in the session file as its OWN record, beside the
// messages, checkpoints and compaction records:
//
//   {"type":"operation","kind":"forkStart","owner":"f-…","turn":null,
//    "boundaryMessageId":"m-…","copied":4,"occurredAt":"…"}
//   {"type":"operation","kind":"forkEnd","owner":"f-…","turn":null,
//    "boundaryMessageId":null,"copied":4,"occurredAt":"…"}
//
// The kind is stored as its NAME, not a number: a marker from a newer build
// must be refused, not guessed at — reading an unknown kind as an "end" would
// release a claim that is still held.

using System.Text.Json.Serialization;

namespace Harbor.Storage.Jsonl;

/// <summary>
///     The <c>"type":"operation"</c> line: one fork/turn crash marker.
/// </summary>
internal sealed record SessionOperationEntry(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("owner")] string? Owner,
    [property: JsonPropertyName("turn")] int? Turn,
    [property: JsonPropertyName("boundaryMessageId")] string? BoundaryMessageId,
    [property: JsonPropertyName("copied")] int Copied,
    [property: JsonPropertyName("occurredAt")] DateTimeOffset OccurredAt);

/// <summary>
///     The one border between the operation lines and the contract records.
/// </summary>
internal static class SessionOperationMapperKit
{
    public static bool TryParseKind(string? value, out SessionOperationKind kind)
    {
        if (!Enum.TryParse(value, ignoreCase: true, out kind))
        {
            return false;
        }

        return Enum.IsDefined(kind);
    }

    public static SessionOperationEvent ToContract(SessionOperationEntry row, SessionOperationKind kind, int sequence) =>
        new()
        {
            Kind = kind,
            Owner = row.Owner,
            Turn = row.Turn,
            BoundaryMessageId = row.BoundaryMessageId,
            Copied = row.Copied,
            OccurredAt = row.OccurredAt,
            Sequence = sequence,
        };

    public static SessionOperationEntry ToRow(SessionOperationEvent operationEvent) =>
        new(
            "operation",
            operationEvent.Kind.ToString(),
            operationEvent.Owner,
            operationEvent.Turn,
            operationEvent.BoundaryMessageId,
            operationEvent.Copied,
            operationEvent.OccurredAt);
}
