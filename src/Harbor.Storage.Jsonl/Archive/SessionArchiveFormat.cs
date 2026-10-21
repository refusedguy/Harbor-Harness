// SessionArchiveFormat.cs — the wire shape of a session archive (#1750).
//
// WHY AN ARCHIVE AND NOT "A COPY OF THE FILE"
// --------------------------------------------
// The JSONL backend stores a session as one file, so "export it" is a copy.
// The SQLite backend stores the same session in tables, so "export it" has no
// answer at all. The archive is the answer that does not depend on the backend:
// ONE canonical JSONL (a header line plus one line per validated log record)
// plus the attachments, plus a manifest that says which format version wrote
// it. Every backend exports the same bytes for the same session.
//
// THE LAYOUT
// ----------
//
//   manifest.json                    the format marker, the version, and the index
//   session.jsonl                    the canonical log: header + one line per record
//   attachments/<sha256>.<ext>       the attachment bytes, content-addressed
//
// The manifest comes first because it is the only file a reader needs in order
// to decide whether it can read the archive at all: the marker says "this is a
// Harbor archive", the version says "this is the shape I understand". A reader
// that meets a version it does not know refuses the archive instead of
// guessing at the lines under it.
//
// THE DIGEST
// ----------
// An attachment is named by the SHA-256 of its BYTES, not by the id the
// session gave it. That is what makes the archive content-addressed: the same
// image attached to three turns is ONE file in the ZIP, referenced three
// times, instead of three copies of the same bytes. The extension is derived
// from the MIME type so a human opening the archive can tell a PNG from a
// JPEG without consulting the manifest.
//
// WHY THE VERSION IS HERE AND NOT IN THE FILENAME
// ------------------------------------------------
// The file name says which session the archive holds; the version says which
// SHAPE it holds. A session id is stable across the life of a session, but
// the format is not: a future build may add a field to the manifest, and a
// reader that met the old file name would have no way to tell. The version
// lives in the manifest, where the reader looks first.

namespace Harbor.Storage.Jsonl;

/// <summary>
///     The names and constants of the session-archive format (#1750).
/// </summary>
internal static class SessionArchiveFormat
{
    /// <summary>
    ///     The marker every archive manifest carries. It is what tells a
    ///     Harbor archive apart from an arbitrary ZIP a user picked up.
    /// </summary>
    internal const string Marker = "$harbor-session-archive";

    /// <summary>
    ///     The format version this build writes. A reader refuses a version
    ///     it does not know rather than guessing at the lines under it.
    /// </summary>
    internal const int Version = 1;

    /// <summary>
    ///     The name of the manifest entry inside the ZIP.
    /// </summary>
    internal const string ManifestEntryName = "manifest.json";

    /// <summary>
    ///     The name of the canonical log entry inside the ZIP.
    /// </summary>
    internal const string LogEntryName = "session.jsonl";

    /// <summary>
    ///     The prefix every content-addressed attachment lives under.
    /// </summary>
    internal const string AttachmentsPrefix = "attachments/";

    /// <summary>
    ///     The digest algorithm the archive names its attachments by. It is
    ///     spelled in the manifest so a future build can say "sha512" without
    ///     a reader having to guess.
    /// </summary>
    internal const string DigestAlgorithm = "sha256";

    /// <summary>
    ///     The log source recorded when the archive was built from the
    ///     store's append-only log: every record the session ever wrote,
    ///     including the ones a rewind shadowed.
    /// </summary>
    internal const string LogSourceLog = "log";

    /// <summary>
    ///     The log source recorded when the archive was built from the
    ///     store's surface: the messages the session shows, and nothing else.
    /// </summary>
    internal const string LogSourceSurface = "surface";
}
