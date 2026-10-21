// SessionArchiveCodec.cs — the JSON shapes of a session archive (#1750).
//
// ONE LINE PER RECORD
// -------------------
// The archive's log is the session log, in the shape a reader can replay: one
// header line, then one line per record, in write order. A message record
// carries the message; a rewind record carries the event that says which
// records left the surface. The two have separate shapes rather than one
// shape with optional members, because a rewind record has no message and a
// message record has no anchor, and a reader that had to tell them apart by
// the presence of a field would be reading a format that can describe a
// record that is neither.
//
// THE STATE ON EVERY LINE
// -----------------------
// A log record is not "the" version of a message on its own: "current" and
// "shadowed" are properties of the log AS A WHOLE, decided by the fold (see
// SessionLogFold). The archive therefore records the fold's verdict on each
// line, so a reader does not have to re-derive it — and cannot get a
// different answer, because the archive is what the fold saw.
//
// THE ATTACHMENT REFERENCE
// ------------------------
// An attachment's bytes live in ONE place in the ZIP, named by their digest.
// The line that used to hold the bytes inline now holds a reference to them.
// That is the whole point of the content-addressed layout: the same image
// attached to three turns costs one file, not three.

using System.Text.Json.Serialization;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Sessions;

namespace Harbor.Storage.Jsonl;

/// <summary>
///     Line 1 of the archive's log: the session record the log belongs to
///     (#1750).
/// </summary>
internal sealed record ArchiveSessionLine(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("version")] int Version,
    [property: JsonPropertyName("session")] Session Session);

/// <summary>
///     One message record of the archive's log (#1750).
/// </summary>
internal sealed record ArchiveMessageLine(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("position")] int Position,
    [property: JsonPropertyName("state")] string State,
    [property: JsonPropertyName("createdAt")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("parentId")] string? ParentId,
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("payload")] object Payload);

/// <summary>
///     One rewind record of the archive's log: the event that says which
///     records left the surface (#1750).
/// </summary>
internal sealed record ArchiveRewindLine(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("position")] int Position,
    [property: JsonPropertyName("state")] string State,
    [property: JsonPropertyName("anchorId")] string? AnchorId,
    [property: JsonPropertyName("occurredAt")] DateTimeOffset? OccurredAt,
    [property: JsonPropertyName("removedCount")] int RemovedCount);

/// <summary>
///     A user message in the archive's shape: the same three fields the
///     session file carries, plus attachments that REFERENCE the archive's
///     content-addressed files instead of holding the bytes inline (#1750).
/// </summary>
internal sealed record ArchiveUserPayload(
    [property: JsonPropertyName("content")] string Content,
    [property: JsonPropertyName("agent")] string Agent,
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("attachments")] ArchiveAttachmentRef[]? Attachments = null);

/// <summary>
///     One attachment, referenced rather than embedded: the digest, the path
///     it lives at inside the ZIP, and the description the session recorded
///     for it (#1750).
/// </summary>
internal sealed record ArchiveAttachmentRef(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("mimeType")] string MimeType,
    [property: JsonPropertyName("width")] int Width,
    [property: JsonPropertyName("height")] int Height,
    [property: JsonPropertyName("sha256")] string Sha256,
    [property: JsonPropertyName("ref")] string Ref,
    [property: JsonPropertyName("sizeBytes")] long SizeBytes);

/// <summary>
///     The names the archive's records use on the wire (#1750).
/// </summary>
internal static class SessionArchiveRecordTypes
{
    /// <summary>The record type of the log's header line.</summary>
    internal const string Session = "session";

    /// <summary>The record type of a message line.</summary>
    internal const string Message = "message";

    /// <summary>The record type of a rewind line.</summary>
    internal const string Rewind = "rewind";
}

/// <summary>
///     The names the archive's log uses for the fold's verdict on a record
///     (#1750).
/// </summary>
internal static class SessionArchiveStates
{
    /// <summary>The record is in the surface: the version the model sees.</summary>
    internal const string Current = "current";

    /// <summary>
    ///     The record is in the log but not in the surface: an earlier
    ///     version of a message, or a message a rewind removed.
    /// </summary>
    internal const string Shadowed = "shadowed";

    /// <summary>The record was never a message: a rewind event.</summary>
    internal const string LogOnly = "logOnly";

    /// <summary>
    ///     The record carries no verdict. A line with this state was written
    ///     by a build that folded the log differently, and a reader keeps it
    ///     rather than guessing.
    /// </summary>
    internal const string Unknown = "unknown";

    /// <summary>The wire name of a fold verdict.</summary>
    internal static string Name(SessionLogEntryState? state) => state switch
    {
        SessionLogEntryState.Current => Current,
        SessionLogEntryState.Shadowed => Shadowed,
        SessionLogEntryState.LogOnly => LogOnly,
        _ => Unknown,
    };

    /// <summary>The fold verdict a wire name stands for.</summary>
    internal static SessionLogEntryState? State(string? name) => name switch
    {
        Current => SessionLogEntryState.Current,
        Shadowed => SessionLogEntryState.Shadowed,
        LogOnly => SessionLogEntryState.LogOnly,
        _ => null,
    };
}
