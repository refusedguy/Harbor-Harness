// SessionArchiveManifest.cs — what a session archive says about itself (#1750).
//
// THE MANIFEST IS THE INDEX, NOT A SECOND COPY OF THE LOG
// --------------------------------------------------------
// The manifest answers three questions and nothing else:
//
//   1. Is this a Harbor archive, and is it a version I can read?
//      ($marker + version)
//   2. What does the log under it hold — how many records, how many of them
//      are shadowed, how many are events rather than messages?
//   3. Which attachment files are in the ZIP, and what is each one's digest?
//
// It does NOT repeat the session record: that lives in the log's own header
// line, in the same place it lives in a session file, so a reader has ONE
// place to look and the two cannot disagree.
//
// WHY THE COUNTS ARE HERE
// -----------------------
// A log line can be unreadable. A record whose bytes are not a complete
// document is skipped, and the transcript says how many were skipped
// (#1744). An archive that carried only the lines it could decode would
// read as "the agent never said that" — the same lie the transcript
// integrity exists to remove. So the manifest carries the counts, and a
// reader can tell a complete archive from a lossy one without guessing.
//
// WHY THE INTEGRITY IS OPTIONAL
// -----------------------------
// A store that does not expose a transcript cannot say what its read could
// not deliver, and a manifest that reported zero unreadable records for
// such a store would be asserting something nobody checked. The field is
// therefore absent, not zero.
//
// WHY THE MEMBERS ARE DOCUMENTED AT THE TYPE
// ------------------------------------------
// A record's POSITIONAL parameter cannot carry an XML doc comment of its
// own — the compiler reports CS1587 for it, because the comment would have
// nothing to attach to. The documentation therefore lives in the type's
// <param> tags, which is where a reader of a record looks for it anyway.

using System.Text.Json.Serialization;

namespace Harbor.Storage.Jsonl;

/// <summary>
///     The manifest of a session archive: the format marker, the version, the
///     shape of the log under it and the index of the attachments beside it
///     (#1750).
/// </summary>
/// <param name="Marker">
///     The format marker. Always <c>$harbor-session-archive</c> in an archive
///     this build wrote.
/// </param>
/// <param name="Version">
///     The format version. A reader refuses a version it does not know.
/// </param>
/// <param name="CreatedAt">When the archive was written.</param>
/// <param name="SessionId">The id of the session the archive holds.</param>
/// <param name="LogSource">
///     Where the log came from: <c>log</c> when the store exposed its
///     append-only log (every record, shadowed ones included), and
///     <c>surface</c> when it did not (the messages the session shows).
/// </param>
/// <param name="LogEntries">How many records the log holds.</param>
/// <param name="CurrentEntries">
///     How many of those records are in the surface — the version of a
///     message the model and the UI see.
/// </param>
/// <param name="ShadowedEntries">
///     How many of those records are shadowed: an earlier version of a
///     message, or a message a rewind took out of the surface. They are in
///     the archive, and they are not what the session shows.
/// </param>
/// <param name="LogOnlyEntries">
///     How many of those records are events rather than messages — the rewind
///     markers.
/// </param>
/// <param name="Integrity">
///     What the store's transcript read could not deliver, when the store
///     exposes a transcript. Null when it does not: an absent field says
///     "nobody checked", and a zero would say "nothing was missing".
/// </param>
/// <param name="Attachments">
///     The attachments the archive holds, one entry per distinct content.
/// </param>
public sealed record SessionArchiveManifest(
    [property: JsonPropertyName("$marker")] string Marker,
    [property: JsonPropertyName("version")] int Version,
    [property: JsonPropertyName("createdAt")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("sessionId")] string SessionId,
    [property: JsonPropertyName("logSource")] string LogSource,
    [property: JsonPropertyName("logEntries")] int LogEntries,
    [property: JsonPropertyName("currentEntries")] int CurrentEntries,
    [property: JsonPropertyName("shadowedEntries")] int ShadowedEntries,
    [property: JsonPropertyName("logOnlyEntries")] int LogOnlyEntries,
    [property: JsonPropertyName("integrity")] SessionArchiveIntegrity? Integrity,
    [property: JsonPropertyName("attachments")] SessionArchiveAttachment[] Attachments);

/// <summary>
///     One attachment in a session archive: the bytes, named by their digest
///     (#1750).
/// </summary>
/// <param name="Sha256">The SHA-256 of the bytes, lower-case hex.</param>
/// <param name="Ref">
///     Where the bytes live inside the ZIP, e.g.
///     <c>attachments/&lt;sha256&gt;.png</c>.
/// </param>
/// <param name="SizeBytes">How many bytes the attachment holds.</param>
/// <param name="MimeType">The MIME type the session recorded for the attachment.</param>
/// <param name="Path">
///     The path the attachment was attached from — provenance only; the file
///     it names is not part of the archive.
/// </param>
/// <param name="Width">The pixel width the session probed, or zero when it could not.</param>
/// <param name="Height">The pixel height the session probed, or zero when it could not.</param>
public sealed record SessionArchiveAttachment(
    [property: JsonPropertyName("sha256")] string Sha256,
    [property: JsonPropertyName("ref")] string Ref,
    [property: JsonPropertyName("sizeBytes")] long SizeBytes,
    [property: JsonPropertyName("mimeType")] string MimeType,
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("width")] int Width,
    [property: JsonPropertyName("height")] int Height);

/// <summary>
///     What a store's transcript read could not deliver, as the archive
///     records it (#1750).
/// </summary>
/// <param name="UnreadableRecords">How many records the read could not decode.</param>
/// <param name="IsTailTruncated">Whether the cut, if there was one, was at the tail of the log.</param>
/// <param name="UnclosedToolCalls">How many tool calls the transcript never closed.</param>
public sealed record SessionArchiveIntegrity(
    [property: JsonPropertyName("unreadableRecords")] int UnreadableRecords,
    [property: JsonPropertyName("isTailTruncated")] bool IsTailTruncated,
    [property: JsonPropertyName("unclosedToolCalls")] int UnclosedToolCalls);
