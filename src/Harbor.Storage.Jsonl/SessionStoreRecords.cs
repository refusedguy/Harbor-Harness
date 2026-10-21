// SessionStoreRecords.cs — JSONL session-file record shapes.
//
// Extracted verbatim from JsonlSessionStore.cs (#184 god-object
// decomposition). The store orchestrates I/O + caching; this file owns the
// on-disk schema (line-1 header, message lines, parsed-message cache entry).

using System.Text.Json.Serialization;
using Harbor.Abstractions.Models;

namespace Harbor.Storage.Jsonl;

/// <summary>
///     Identity of a session file's on-disk state: its last-write timestamp
///     plus its length. <b>Both halves are required (#459)</b> — the timestamp
///     alone is not a safe freshness key, because a filesystem with coarse
///     timestamps can serve a write whose mtime lands in the same tick as the
///     previous one, which turned a stale cache entry into a permanent hit on
///     outdated content. One <see cref="FileInfo" /> yields both fields from a
///     single stat, so the key costs no extra syscall over mtime alone.
/// </summary>
internal readonly record struct SessionFileStat(DateTimeOffset LastWriteUtc, long Length)
{
    /// <summary>Read the stat of <paramref name="path" /> in one filesystem call.</summary>
    internal static SessionFileStat Read(string path)
    {
        var info = new FileInfo(path);
        return new SessionFileStat(info.LastWriteTimeUtc, info.Length);
    }

    /// <summary>True when both fields match — the file is the same one we cached.</summary>
    internal bool Matches(SessionFileStat other) =>
        LastWriteUtc == other.LastWriteUtc && Length == other.Length;
}

/// <summary>
///     Parsed-message cache entry (§3.3). Records the <see cref="SessionFileStat" />
///     the parse was taken from so subsequent reads can detect freshness with a
///     single stat.
/// </summary>
/// <param name="FileLastWriteUtc">The file's last-write stamp the parse was taken from.</param>
/// <param name="FileLength">The file's length the parse was taken from.</param>
/// <param name="Surface">
///     The SURFACE: the messages the log still shows — what
///     <c>GetMessagesAsync</c> hands back. A fork copies them and the stats
///     fold over them, so nothing may be added to them behind the store's back.
/// </param>
/// <param name="Transcript">
///     The READER's view of the same read: the messages with the turns the log
///     never closed closed in memory, plus what the read could not deliver
///     (#1744). Derived from <paramref name="Surface" /> and never written
///     back.
/// </param>
/// <param name="Log">
///     The whole log: every record the file holds, in write order (#1752).
/// </param>
internal sealed record SessionCacheEntry(
    DateTimeOffset FileLastWriteUtc,
    long FileLength,
    IReadOnlyList<AgentMessage> Surface,
    SessionTranscript Transcript,
    IReadOnlyList<SessionLogEntry> Log)
{
    /// <summary>True when this entry was parsed from exactly this file state.</summary>
    internal bool IsFreshFor(SessionFileStat stat) =>
        FileLength == stat.Length && FileLastWriteUtc == stat.LastWriteUtc;
}

/// <summary>
///     Outcome of a full-file read: the parsed messages plus the
///     <see cref="SessionFileStat" /> they were parsed from, and whether that
///     pairing is <em>provable</em> (#459).
/// </summary>
/// <remarks>
///     <para>
///         <see cref="IsStable" /> is false when the file changed while it was
///         being read — a concurrent append grew it, or an atomic rewrite
///         swapped the path — so the messages are a consistent prefix of the
///         log but not provably the whole of the state the stat names. Such a
///         snapshot is still worth returning (it never contains a record cut in
///         half), but caching it would publish incomplete data under a
///         timestamp that claims completeness: the next read hits the cache and
///         the missing message is invisible until the next write or restart.
///     </para>
///     <para>
///         <b>#1785:</b> <see cref="Header" /> rides along with the messages
///         because the export needs BOTH from the SAME pass. A second read of
///         the header would be a second generation: the header could be
///         rewritten (a title edit) between the two reads, and the export
///         would carry a header that contradicts the history under it.
///     </para>
///     <para>
///         <b>#1744:</b> <see cref="UnreadableRecordCount" /> and
///         <see cref="IsTailTruncated" /> say what the read could NOT
///         deliver. A record whose bytes are not a complete document is
///         skipped — its content is unknowable — but the skip is counted and
///         reported, so it never reads as "the agent never said that".
///     </para>
///     <para>
///         <b>#1752:</b> <see cref="Log" /> rides along too — the WHOLE log,
///         every record the file holds, in write order. The messages are the
///         surface's view of that log (one record per id); the log is the
///         truth, and it is what "what did the agent actually say?" is
///         answered from.
///     </para>
/// </remarks>
internal sealed record MessageReadSnapshot(
    IReadOnlyList<AgentMessage> Messages,
    SessionFileStat Stat,
    bool IsStable,
    SessionHeaderEntry? Header = null,
    int UnreadableRecordCount = 0,
    bool IsTailTruncated = false,
    IReadOnlyList<SessionLogEntry>? Log = null);

/// <summary>
///     One cold read of a session: the surface's messages, the whole log and
///     the reader's view of the same read (#1744). All of it comes from ONE
///     pass over the file — a second read would be a second generation, and
///     the halves could describe different moments.
/// </summary>
/// <param name="Surface">The surface: the messages the log still shows.</param>
/// <param name="Transcript">The reader's view: the closed transcript and its integrity.</param>
/// <param name="Log">The whole log: every record the file holds, in write order.</param>
internal sealed record SessionReadResult(
    IReadOnlyList<AgentMessage> Surface,
    SessionTranscript Transcript,
    IReadOnlyList<SessionLogEntry> Log);

/// <summary>
///     Line-1 header of a session file. Optional trailing fields carry the
///     newer <see cref="Harbor.Abstractions.Models.Session"/> attributes — before they
///     existed, UpdateAsync rewrote the header WITHOUT parent linkage/status/git/kind
///     fields and silently dropped them on first rename/rebind (V4-bugfix).
///     Defaults keep legacy files parseable.
/// </summary>
/// <remarks>
///     <para>
///         <b>#1740:</b> <see cref="Sharing" /> says which TIER wrote the file
///         (see <see cref="SessionLogTier" />). It is omitted entirely for the
///         local tier, so a log nobody asked to be shared keeps the exact bytes
///         it had before the tier existed, and it is present on a keyless log so
///         a reader can tell a shareable artifact from a private notebook.
///     </para>
/// </remarks>
internal sealed record SessionHeaderEntry(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("version")] int Version,
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("projectId")] string ProjectId,
    [property: JsonPropertyName("directory")] string Directory,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("agent")] string Agent,
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("providerId")] string ProviderId,
    [property: JsonPropertyName("createdAt")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("updatedAt")] DateTimeOffset UpdatedAt = default,
    [property: JsonPropertyName("parentSessionId")] string? ParentSessionId = null,
    [property: JsonPropertyName("status")] SessionStatus Status = SessionStatus.Idle,
    [property: JsonPropertyName("gitBranch")] string? GitBranch = null,
    [property: JsonPropertyName("gitIsDirty")] bool GitIsDirty = false,
    [property: JsonPropertyName("kind")] SessionKind Kind = SessionKind.User,
    // #1740: the tier marker. Omitted when the tier is the local one, so the
    // header of an ordinary log is byte-identical to what it was before. The
    // JsonIgnore attribute (rather than the context-wide ignore rule) is
    // required, because the store serializes through the source-generated
    // metadata, which does not inherit JsonlCodecContext.JsonOptions.
    [property: JsonPropertyName("sharing")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Sharing = null);

/*
 * DDD-audit 25.08 (ROP-C Z3): <see cref="JsonlSessionStore.GetAsync" /> used to
 * fabricate Session.UpdatedAt = UtcNow on EVERY read, which made ListAsync's
 * recency sort random — the same session reordered between calls. The stored
 * header now carries the real last-activity timestamp; reads never invent one.
 */

internal sealed record MessageEntry(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("parentId")] string? ParentId,
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("createdAt")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("payload")] object Payload);
