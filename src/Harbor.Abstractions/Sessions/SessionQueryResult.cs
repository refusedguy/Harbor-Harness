namespace Harbor.Abstractions.Sessions;

/// <summary>
///     One record a session query matched.
/// </summary>
/// <remarks>
///     <para>
///         A hit is a DETACHED copy: it holds the text and the metadata of
///         the record it matched, never a live handle into the store's state.
///         The reader therefore cannot observe the log changing under it.
///     </para>
/// </remarks>
/// <param name="Kind">The kind of record this hit is.</param>
/// <param name="SessionId">The owning session.</param>
/// <param name="RecordId">
///     The record's own id — the message id, the event id, or the session id
///     for a session record.
/// </param>
/// <param name="Timestamp">
///     The instant the record is filed under: the creation instant of a
///     message, the last-activity instant of a session, the emission instant
///     of an event.
/// </param>
/// <param name="Text">
///     The searchable text of the record (empty for a record that carries no
///     text, e.g. a session header without a title).
/// </param>
/// <param name="Score">
///     The relevance score of the hit; HIGHER is a better match. The scale is
///     the backend's: a full-text relevance score (the negated BM25 of the
///     index) or, for a literal scan, the negated offset of the first match.
///     A full-text score and a literal score are different scales — compare
///     scores within one mode and one kind, never across them.
/// </param>
/// <param name="MatchPosition">
///     The character offset of the first text match inside
///     <paramref name="Text" />, or -1 when the hit carries no text match.
/// </param>
/// <param name="Role">
///     The message role, for a <see cref="SessionRecordKind.Message" /> hit.
/// </param>
/// <param name="EventType">
///     The event type name, for a <see cref="SessionRecordKind.Event" /> hit.
/// </param>
public sealed record SessionQueryHit(
    SessionRecordKind Kind,
    string SessionId,
    string RecordId,
    DateTimeOffset Timestamp,
    string Text,
    double Score,
    int MatchPosition,
    string? Role = null,
    string? EventType = null);

/// <summary>
///     The outcome of a session query.
/// </summary>
/// <param name="Hits">
///     The matched records, ordered by the query's rank and cut to the
///     query's limit. Empty when nothing matched.
/// </param>
/// <param name="TotalMatches">
///     How many records matched the query BEFORE the limit was applied. It is
///     the number a caller shows next to a truncated list.
/// </param>
/// <param name="Truncated">
///     True when <see cref="TotalMatches" /> exceeds the number of returned
///     hits — the limit (or the offset) cut the result set.
/// </param>
/// <param name="Mode">
///     How the backend matched the text filter — the caller learns which
///     ranking it is reading.
/// </param>
/// <param name="SupportedKinds">
///     The record kinds this backend can serve. A kind that is missing here
///     was not searched (e.g. no backend persists events yet), which is not
///     the same as "nothing matched".
/// </param>
public sealed record SessionQueryResult(
    IReadOnlyList<SessionQueryHit> Hits,
    int TotalMatches,
    bool Truncated,
    SessionQueryMode Mode,
    IReadOnlyList<SessionRecordKind> SupportedKinds);
