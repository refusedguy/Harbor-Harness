namespace Harbor.Abstractions.Sessions;

/// <summary>
///     The kind of record a session query can select (#1743).
/// </summary>
/// <remarks>
///     <para>
///         A query reads a session LOG, and the log holds more than one kind of
///         record: the session header itself, the persisted messages, and —
///         once a backend persists the event stream — the agent events.
///     </para>
///     <para>
///         <see cref="Event" /> is part of the contract before any backend
///         stores events: the filter vocabulary an event log will be queried
///         with. A backend that does not persist events yet does NOT list
///         <see cref="Event" /> in
///         <see cref="SessionQueryResult.SupportedKinds" />, so a caller can
///         tell "no events matched" from "this backend has no events".
///     </para>
/// </remarks>
public enum SessionRecordKind
{
    /// <summary>A session header — one record per conversation.</summary>
    Session = 0,

    /// <summary>A persisted message of a session.</summary>
    Message = 1,

    /// <summary>An agent event of a session (needs an event-log backend).</summary>
    Event = 2,
}

/// <summary>
///     How a query orders its hits.
/// </summary>
public enum SessionQueryRank
{
    /// <summary>
    ///     Best match first: the backend's relevance measure — a full-text
    ///     relevance score where a full-text index exists, the position of the
    ///     first text match otherwise. The default.
    /// </summary>
    Relevance = 0,

    /// <summary>Oldest record first (chronological).</summary>
    OldestFirst = 1,

    /// <summary>Newest record first (reverse chronological).</summary>
    NewestFirst = 2,
}

/// <summary>
///     How a backend matched the text filter of a query.
/// </summary>
/// <remarks>
///     <para>
///         The mode is part of the RESULT, not a promise of the contract: a
///         backend that has a full-text index reports
///         <see cref="FullText" />, a backend that could only scan literals
///         reports <see cref="Literal" />. The caller learns which ranking it
///         is looking at instead of assuming one.
///     </para>
///     <para>
///         The two modes also differ in WHAT they match: the full-text path
///         matches TOKENS (a query is a phrase of tokens), the literal path
///         matches SUBSTRINGS. A query that must behave identically on every
///         backend is a substring query.
///     </para>
/// </remarks>
public enum SessionQueryMode
{
    /// <summary>Matched through a full-text index and ranked by relevance.</summary>
    FullText = 0,

    /// <summary>Matched by a literal scan of the candidate records.</summary>
    Literal = 1,
}

/// <summary>
///     The time window of a query: the inclusive instant range the query
///     selects ("the records between X and Y").
/// </summary>
/// <remarks>
///     Both bounds are INCLUSIVE and either may be omitted — a window with
///     only a start is "from X onwards", a window with only an end is "up to
///     Y". The window applies to the timestamp of the record: the creation
///     instant of a message, the last-activity instant of a session, and the
///     emission instant of an event.
/// </remarks>
/// <param name="From">Inclusive lower bound, or null for "no lower bound".</param>
/// <param name="To">Inclusive upper bound, or null for "no upper bound".</param>
public sealed record SessionQueryWindow(DateTimeOffset? From, DateTimeOffset? To)
{
    /// <summary>The window that selects every record.</summary>
    public static SessionQueryWindow Unbounded { get; } = new(null, null);

    /// <summary>
    ///     True when the window selects nothing — its start is after its end.
    ///     Such a window is a rejected query, not an empty result.
    /// </summary>
    public bool IsEmpty => From.HasValue && To.HasValue && From.Value > To.Value;

    /// <summary>
    ///     Whether <paramref name="instant" /> falls inside the window
    ///     (both bounds inclusive).
    /// </summary>
    /// <param name="instant">The instant to test.</param>
    /// <returns>True when the instant is inside the window.</returns>
    public bool Contains(DateTimeOffset instant)
    {
        if (From.HasValue && instant < From.Value)
            return false;
        if (To.HasValue && instant > To.Value)
            return false;
        return true;
    }
}

/// <summary>
///     The options of a session query: what to select, over which window,
///     with which text, and how much of it.
/// </summary>
/// <remarks>
///     <para>
///         <b>Filter algebra (#1743):</b> clauses are AND-ed together, the
///         values INSIDE one clause are OR-ed, and an EMPTY value list means
///         "nothing matches" — not "no filter". A query with
///         <see cref="SessionIds" /> set to an empty list therefore returns
///         no hits, which is what a caller that computed an empty
///         intersection expects.
///     </para>
///     <para>
///         <b>Text semantics:</b> the text filter is a phrase. A backend with
///         a full-text index matches it as a sequence of tokens; a backend
///         without one matches it as a substring. The mode the backend used is
///         reported in <see cref="SessionQueryResult.Mode" />.
///     </para>
///     <para>
///         The contract is AOT-friendly by construction: plain records, plain
///         collections, no reflection and no <c>dynamic</c>.
///     </para>
/// </remarks>
public sealed record SessionQueryOptions
{
    /// <summary>The smallest accepted <see cref="Limit" />.</summary>
    public const int MinLimit = 1;

    /// <summary>The largest accepted <see cref="Limit" />.</summary>
    public const int MaxLimit = 1000;

    /// <summary>
    ///     Session ids to select. Null means "every session"; an empty list
    ///     means "no session".
    /// </summary>
    public IReadOnlyList<string>? SessionIds { get; init; }

    /// <summary>
    ///     Record kinds to select. Null means "every kind the backend
    ///     supports"; an empty list means "no kind".
    /// </summary>
    public IReadOnlyList<SessionRecordKind>? RecordKinds { get; init; }

    /// <summary>
    ///     Event type names to select (the discriminator names of the event
    ///     log, e.g. <c>tool_execution_start</c>). Null means "every event
    ///     type"; an empty list means "no event type". The clause applies to
    ///     <see cref="SessionRecordKind.Event" /> records only.
    /// </summary>
    public IReadOnlyList<string>? EventTypes { get; init; }

    /// <summary>
    ///     Message roles to select (e.g. <c>user</c>, <c>assistant</c>,
    ///     <c>tool_result</c>). Null means "every role"; an empty list means
    ///     "no role". The clause applies to
    ///     <see cref="SessionRecordKind.Message" /> records only.
    /// </summary>
    public IReadOnlyList<string>? Roles { get; init; }

    /// <summary>
    ///     The time window — the inclusive instant range the query selects.
    ///     Null means "every instant".
    /// </summary>
    public SessionQueryWindow? Window { get; init; }

    /// <summary>
    ///     The text filter: the phrase to find in the record's searchable
    ///     text. Null means "no text filter"; a blank one is a rejected query.
    /// </summary>
    public string? Text { get; init; }

    /// <summary>
    ///     How many hits to return at most. Must be within
    ///     <see cref="MinLimit" />..<see cref="MaxLimit" />.
    /// </summary>
    public int Limit { get; init; } = 50;

    /// <summary>
    ///     How many hits to skip before the returned window starts.
    /// </summary>
    public int Offset { get; init; }

    /// <summary>
    ///     How the hits are ordered.
    /// </summary>
    public SessionQueryRank Rank { get; init; } = SessionQueryRank.Relevance;

    /// <summary>
    ///     Check the options before they reach a backend. A rejected query
    ///     travels the <see cref="Result" /> rail as a failure — it is an
    ///     expected outcome, not an exception.
    /// </summary>
    /// <returns>Success, or a failure naming the offending option.</returns>
    public Result Validate()
    {
        if (Limit < MinLimit)
            return Result.Failure($"Query limit must be at least {MinLimit} (got {Limit}).");
        if (Limit > MaxLimit)
            return Result.Failure($"Query limit must not exceed {MaxLimit} (got {Limit}).");
        if (Offset < 0)
            return Result.Failure($"Query offset must not be negative (got {Offset}).");
        if (Window is { } window && window.IsEmpty)
            return Result.Failure("Query window start must not be after its end.");
        if (Text is not null && string.IsNullOrWhiteSpace(Text))
            return Result.Failure("Query text must not be blank.");
        if (RecordKinds is not null)
        {
            for (int i = 0; i < RecordKinds.Count; i++)
            {
                if (!Enum.IsDefined(RecordKinds[i]))
                    return Result.Failure($"Unknown session record kind '{(int)RecordKinds[i]}'.");
            }
        }

        if (!ValidateValues(SessionIds))
            return Result.Failure("Session id filter must not contain an empty value.");
        if (!ValidateValues(Roles))
            return Result.Failure("Role filter must not contain an empty value.");
        if (!ValidateValues(EventTypes))
            return Result.Failure("Event type filter must not contain an empty value.");

        return Result.Success();
    }

    private static bool ValidateValues(IReadOnlyList<string>? values)
    {
        if (values is null)
            return true;

        for (int i = 0; i < values.Count; i++)
        {
            if (string.IsNullOrWhiteSpace(values[i]))
                return false;
        }

        return true;
    }
}
