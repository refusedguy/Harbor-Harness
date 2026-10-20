// SessionLogEntry.cs — the append-only session-log record of #1784.
//
// A session LOG is the whole truth of a session: every record ever written, in
// write order. The SURFACE (what the model sees and what the UI shows) is a
// FOLD of that log — see SessionSurface.cs.
//
// Two kinds of record exist in the target model:
//
//   message   an AgentMessage appended to the log. A second record with the
//             same id is a NEW VERSION of that message, not a replacement of
//             it: the earlier version stays in the log and becomes shadowed.
//   rewind    an EVENT: the tail after an anchor left the surface. The records
//             themselves stay in the log — the event is the record of their
//             leaving, and it is what makes an append-only log able to say
//             "rewound" without rewriting a line it has already written.
//
// There is deliberately no "deleted" record and no tombstone: append-only
// cannot rewrite history, and the whole point of the model is that the text
// the agent produced is never physically gone (#1784).

using Harbor.Abstractions.Models;

namespace Harbor.Abstractions.Sessions;

/// <summary>
///     What kind of record a session-log entry is.
/// </summary>
public enum SessionLogEntryKind
{
    /// <summary>
    ///     A message record — an <see cref="AgentMessage" /> written to the log.
    /// </summary>
    Message,

    /// <summary>
    ///     A rewind event — the tail after an anchor left the surface. The
    ///     records themselves stay in the log; the event is what records their
    ///     leaving.
    /// </summary>
    Rewind,
}

/// <summary>
///     How a log entry relates to the current surface, as decided by
///     <see cref="SessionLogFold" />.
/// </summary>
public enum SessionLogEntryState
{
    /// <summary>
    ///     In the surface: the version of the message the model and the UI see.
    /// </summary>
    Current,

    /// <summary>
    ///     In the log, not in the surface — an earlier version of a message, or
    ///     a message a rewind removed. Physically present, not gone.
    /// </summary>
    Shadowed,

    /// <summary>
    ///     Never was a message: a log-only record (a rewind event).
    /// </summary>
    LogOnly,
}

/// <summary>
///     One record in a session's append-only log.
/// </summary>
/// <remarks>
///     <para>
///         The record is the unit the store reads and writes, and the unit the
///         fold classifies. It is deliberately dumb: it carries no verdict of
///         its own, because "current" and "shadowed" are properties of the log
///         as a whole, not of a record in isolation.
///     </para>
///     <para>
///         <see cref="Position" /> is the record's ordinal index in write
///         order. It is what lets a rewind say "between the anchor and me"
///         without a sequence field in the on-disk format: an append-only log
///         is already monotonic, so the position IS the sequence.
///     </para>
/// </remarks>
public sealed record SessionLogEntry
{
    /// <summary>
    ///     What kind of record this is.
    /// </summary>
    public required SessionLogEntryKind Kind { get; init; }

    /// <summary>
    ///     The record's id: the message id for a message record, the event id
    ///     for a rewind event.
    /// </summary>
    public required string Id { get; init; }

    /// <summary>
    ///     The record's position in the log — its ordinal index in write order.
    /// </summary>
    public required int Position { get; init; }

    /// <summary>
    ///     The message, for a message record. Null for a rewind event.
    /// </summary>
    public AgentMessage? Message { get; init; }

    /// <summary>
    ///     The anchor message id, for a rewind event: the message the tail was
    ///     cut at. Null for a message record.
    /// </summary>
    public string? AnchorId { get; init; }

    /// <summary>
    ///     When the event happened, for a rewind event. Null for a message
    ///     record (the message carries its own timestamp).
    /// </summary>
    public DateTimeOffset? OccurredAt { get; init; }

    /// <summary>
    ///     How many messages left the surface because of a rewind event. Zero
    ///     for a message record.
    /// </summary>
    public int RemovedCount { get; init; }

    /// <summary>
    ///     The classification the fold assigned to this record: null until the
    ///     record has been folded. A raw log read straight from a store is
    ///     unclassified — "current" is a property of the whole log, so only
    ///     <see cref="SessionLogFold" /> can decide it.
    /// </summary>
    public SessionLogEntryState? State { get; init; }
}
