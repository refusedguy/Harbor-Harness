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

using System.Text.Json.Serialization;

namespace Harbor.Storage.Jsonl;

/// <summary>
///     The manifest of a session archive: the format marker, the version, the
///     shape of the log under it and the index of the attachments beside it
///     (#1750).
/// </summary>
public sealed record SessionArchiveManifest(
    /// <summary>
    ///     The format marker. Always <c>$harbor-session-archive</c> in an
    ///     archive this build wrote.
    /// </summary>
    [property: JsonPropertyName("$marker")] string Marker,
    /// <summary>
    ///     The format version. A reader refuses a version it does not know.
    /// </summary>
    [property: JsonPropertyName("version")] int Version,
    /// <summary>
    ///     When the archive was written.
    /// </summary>
    [property: JsonPropertyName("createdAt")] DateTimeOffset CreatedAt,
    /// <summary>
    ///     The id of the session the archive holds.
    /// </summary>
    [property: JsonPropertyName("sessionId")] string SessionId,
    /// <summary>
    ///     Where the log came from: <c>log</c> when the store exposed its
    ///     append-only log (every record, shadowed ones included), and
    ///     <c>surface</c> when it did not (the messages the session shows).
    /// </summary>
    [property: JsonPropertyName("logSource")] string LogSource,
    /// <summary>
    ///     How many records the log holds.
    /// </summary>
    [property: JsonPropertyName("logEntries")] int LogEntries,
    /// <summary>
    ///     How many of those records are in the surface — the version of a
    ///     message the model and the UI see.
    /// </summary>
    [property: JsonPropertyName("currentEntries")] int CurrentEntries,
    /// <summary>
    ///     How many of those records are shadowed: an earlier version of a
    ///     message, or a message a rewind took out of the surface. They are
    ///     in the archive, and they are not what the session shows.
    /// </summary>
    [property: JsonPropertyName("shadowedEntries")] int ShadowedEntries,
    /// <summary>
    ///     How many of those records are events rather than messages — the
    ///     rewind markers.
    /// </summary>
    [property: JsonPropertyName("logOnlyEntries")] int LogOnlyEntries,
    /// <summary>
    ///     What the store's transcript read could not deliver, when the store
    ///     exposes a transcript. Null when it does not: an absent field says
    ///     "nobody checked", and a zero would say "nothing was missing".
    /// </summary>
    [property: JsonPropertyName("integrity")] SessionArchiveIntegrity? Integrity,
    /// <summary>
    ///     The attachments the archive holds, one entry per distinct content.
    /// </summary>
    [property: JsonPropertyName("attachments")] SessionArchiveAttachment[] Attachments);

/// <summary>
///     One attachment in a session archive: the bytes, named by their digest
///     (#1750).
/// </summary>
public sealed record SessionArchiveAttachment(
    /// <summary>
    ///     The SHA-256 of the bytes, lower-case hex.
    /// </summary>
    [property: JsonPropertyName("sha256")] string Sha256,
    /// <summary>
    ///     Where the bytes live inside the ZIP, e.g.
    ///     <c>attachments/&lt;sha256&gt;.png</c>.
    /// </summary>
    [property: JsonPropertyName("ref")] string Ref,
    /// <summary>
    ///     How many bytes the attachment holds.
    /// </summary>
    [property: JsonPropertyName("sizeBytes")] long SizeBytes,
    /// <summary>
    ///     The MIME type the session recorded for the attachment.
    /// </summary>
    [property: JsonPropertyName("mimeType")] string MimeType,
    /// <summary>
    ///     The path the attachment was attached from — provenance only; the
    ///     file it names is not part of the archive.
    /// </summary>
    [property: JsonPropertyName("path")] string Path,
    /// <summary>
    ///     The pixel width the session probed, or zero when it could not.
    /// </summary>
    [property: JsonPropertyName("width")] int Width,
    /// <summary>
    ///     The pixel height the session probed, or zero when it could not.
    /// </summary>
    [property: JsonPropertyName("height")] int Height);

/// <summary>
///     What a store's transcript read could not deliver, as the archive
///     records it (#1750).
/// </summary>
public sealed record SessionArchiveIntegrity(
    /// <summary>
    ///     How many records the read could not decode.
    /// </summary>
    [property: JsonPropertyName("unreadableRecords")] int UnreadableRecords,
    /// <summary>
    ///     Whether the cut, if there was one, was at the tail of the log.
    /// </summary>
    [property: JsonPropertyName("isTailTruncated")] bool IsTailTruncated,
    /// <summary>
    ///     How many tool calls the transcript never closed.
    /// </summary>
    [property: JsonPropertyName("unclosedToolCalls")] int UnclosedToolCalls);
