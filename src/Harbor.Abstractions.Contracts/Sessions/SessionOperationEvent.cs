// SessionOperationEvent.cs — the durable crash-marker record of a session
// operation (#2226).
//
// WHY A RECORD
// ------------
// A fork and a turn are operations that write several records. A process that
// dies between the first and the last write leaves a partial result on disk
// that reads like a complete one: a child with half its history, or a turn
// that looks as if it never ran. The operation is therefore bracketed in the
// session log the way compaction is bracketed (#1767):
//
//   ForkStart / TurnStart   written FIRST, before any other record of the
//                           operation. This takes the durable claim.
//   ForkEnd   / TurnEnd     written LAST, after everything the operation
//                           wrote. This releases the claim.
//
// Releasing LAST is the whole engineering point: a crash in between leaves an
// UNMATCHED start, which is detectable evidence that the operation did not
// finish. Writing the end first would leave an end that falsely claims the
// operation succeeded.
//
// The record is log-only: it never enters the model context. It says what
// happened to the session, not what the model said.

namespace Harbor.Abstractions.Sessions;

/// <summary>
///     What kind of operation marker an event is (#2226).
/// </summary>
public enum SessionOperationKind
{
    /// <summary>A fork took its durable claim. Written before any copied message.</summary>
    ForkStart,

    /// <summary>A fork released its durable claim. Written after the copied messages.</summary>
    ForkEnd,

    /// <summary>A turn took its durable claim. Written before the model request.</summary>
    TurnStart,

    /// <summary>A turn released its durable claim. Written after the turn finished.</summary>
    TurnEnd,
}

/// <summary>
///     One durable record of a fork or a turn (#2226).
/// </summary>
/// <remarks>
///     The record is deliberately dumb: whether a claim is HELD is a property
///     of the whole event list, and only <see cref="SessionOperationMarkers" />
///     decides it. A start carries the facts a reader needs to understand the
///     crash; an end repeats the identity so the pair can be matched.
/// </remarks>
public sealed record SessionOperationEvent
{
    /// <summary>What kind of marker this is: a claim being taken or released.</summary>
    public required SessionOperationKind Kind { get; init; }

    /// <summary>
    ///     The turn number, on a turn marker. <see langword="null" /> on a fork marker.
    /// </summary>
    public int? Turn { get; init; }

    /// <summary>
    ///     The claim's identity: a per-fork id on a fork marker, so two forks
    ///     of the same child can be told apart. Null on a turn marker, where
    ///     <see cref="Turn" /> is the identity.
    /// </summary>
    public string? Owner { get; init; }

    /// <summary>
    ///     The inclusive last message id a fork copied, on a fork start.
    /// </summary>
    public string? BoundaryMessageId { get; init; }

    /// <summary>
    ///     How many messages a fork copied, on a fork marker.
    /// </summary>
    public int Copied { get; init; }

    /// <summary>When the marker was written.</summary>
    public DateTimeOffset OccurredAt { get; init; }

    /// <summary>
    ///     The record's position among the session's operation records,
    ///     counted from zero in write order.
    /// </summary>
    public int Sequence { get; init; }
}

/// <summary>
///     The fold that decides whether an operation claim is still held (#2226).
/// </summary>
/// <remarks>
///     A start is matched with an end by identity: <see cref="SessionOperationEvent.Owner" />
///     for a fork, <see cref="SessionOperationEvent.Turn" /> for a turn. An
///     unmatched start is a durable claim nobody released, which is what a
///     crash in the middle of the operation leaves behind.
/// </remarks>
public static class SessionOperationMarkers
{
    /// <summary>
    ///     The starts in <paramref name="events" /> that have no matching end.
    /// </summary>
    /// <param name="events">The session's operation records, in write order.</param>
    /// <param name="start">The kind that takes the claim.</param>
    /// <param name="end">The kind that releases it.</param>
    /// <returns>The unmatched starts, in write order.</returns>
    public static IReadOnlyList<SessionOperationEvent> FindUnmatchedStarts(
        IReadOnlyList<SessionOperationEvent> events,
        SessionOperationKind start,
        SessionOperationKind end)
    {
        var released = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < events.Count; i++)
        {
            SessionOperationEvent evt = events[i];
            if (evt.Kind == end)
            {
                released.Add(Key(evt));
            }
        }

        var open = new List<SessionOperationEvent>();
        var claimed = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < events.Count; i++)
        {
            SessionOperationEvent evt = events[i];
            if (evt.Kind != start)
            {
                continue;
            }

            string key = Key(evt);
            if (!released.Contains(key) && claimed.Add(key))
            {
                open.Add(evt);
            }
        }

        return open;
    }

    /// <summary>True when the child holds an unreleased fork claim.</summary>
    public static bool HasIncompleteFork(IReadOnlyList<SessionOperationEvent> events) =>
        FindUnmatchedStarts(events, SessionOperationKind.ForkStart, SessionOperationKind.ForkEnd).Count > 0;

    /// <summary>
    ///     The turn number of an unreleased turn claim, or null when none is
    ///     open. When several are open the newest one is reported.
    /// </summary>
    public static int? FindIncompleteTurn(IReadOnlyList<SessionOperationEvent> events)
    {
        IReadOnlyList<SessionOperationEvent> open =
            FindUnmatchedStarts(events, SessionOperationKind.TurnStart, SessionOperationKind.TurnEnd);
        return open.Count == 0 ? null : open[^1].Turn;
    }

    private static string Key(SessionOperationEvent evt) =>
        evt.Owner is { } owner ? $"o:{owner}" : $"t:{evt.Turn}";
}
