// SessionSurface.cs — the shared fold of a session log (#1784, PR-1).
//
// ONE fold, three classes, exactly as the target model of #1784 describes it:
//
//   log:        #1  #2  #3  #4  #5  #6
//   current:    m1(#2)  m2(#3)  m4(#6)
//   shadowed:   m3(#4)
//   log-only:   rewind(#5)
//
// The rules, in the order they are applied:
//
//   1. CURRENT BY ID. The last record with a given id is the current version
//      of that message; every earlier version of the same id is shadowed.
//      This is what makes an in-place EDIT non-destructive: the edited message
//      is appended as a new version and the previous one stays in the log.
//   2. REWIND. A rewind event at position R anchored at message A hides every
//      message record whose position lies in (pos(A), R). Records written
//      AFTER the event are untouched — that is what makes "rewind and carry
//      on" work: a message written after the rewind is current again.
//   3. LOG-ONLY. A record that was never a message (a rewind event) is
//      log-only. It is in the log and in no surface, ever.
//
// WHY THE FOLD LIVES HERE AND NOT IN A BACKEND
// ---------------------------------------------
// The rules above are a property of the LOG, not of a file format: the JSONL
// backend, the memory backend and (later) the SQLite backend must all classify
// the same log the same way, or "the surface" would mean one thing per
// backend. So the fold is a pure function over records, lives in the contract
// layer, and every backend calls it.
//
// COMPACTION IS NOT PART OF THIS FOLD
// -----------------------------------
// Compaction does not destroy anything: it APPENDS a summary message
// (Harbor.Application's CompactionBehavior) and hides the folded messages from
// the MODEL CONTEXT only (CompactionPolicy.MaterializeCompactedView). The
// folded messages are physically in the log — today and after this change.
// So the compaction fold is a DOMAIN fold, applied on top of this one, and the
// two are composed at exactly one place: surface = compactionFold(storageFold(log)).

using Harbor.Abstractions.Models;

namespace Harbor.Abstractions.Sessions;

/// <summary>
///     A folded session log: the whole log, plus the surface it produces and
///     the records that are in the log but not in the surface.
/// </summary>
/// <remarks>
///     <para>
///         <see cref="Current" /> is what the model and the UI see. Everything
///         else in the log is still there — that is the difference between
///         "shadowed" and "gone", and it is the whole point of the model.
///     </para>
///     <para>
///         <see cref="Warnings" /> carries the fail-open decisions the fold
///         made: a rewind whose anchor cannot be found, an out-of-order log.
///         They are warnings rather than errors because the alternative — hiding
///         the wrong records — is not reversible for the user, while showing one
///         record too many is.
///     </para>
/// </remarks>
public sealed record SessionSurface
{
    /// <summary>
    ///     The whole log, in write order, each record classified.
    /// </summary>
    public required IReadOnlyList<SessionLogEntry> Entries { get; init; }

    /// <summary>
    ///     The messages in the surface — the current version of every message
    ///     the log still shows.
    /// </summary>
    public required IReadOnlyList<AgentMessage> Current { get; init; }

    /// <summary>
    ///     The message records that are in the log but not in the surface: older
    ///     versions of a message, and messages a rewind removed. Physically
    ///     present.
    /// </summary>
    public required IReadOnlyList<SessionLogEntry> Shadowed { get; init; }

    /// <summary>
    ///     The records that were never messages — the rewind events.
    /// </summary>
    public required IReadOnlyList<SessionLogEntry> LogOnly { get; init; }

    /// <summary>
    ///     The fail-open decisions the fold made, one sentence each.
    /// </summary>
    public required IReadOnlyList<string> Warnings { get; init; }
}

/// <summary>
///     The shared fold of a session log: records in, surface out.
/// </summary>
public static class SessionLogFold
{
    /// <summary>
    ///     Fold a session log into its surface.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The log is expected in write order with ascending
    ///         <see cref="SessionLogEntry.Position" /> values; an out-of-order
    ///         log is folded in array order anyway and reported as a warning.
    ///     </para>
    ///     <para>
    ///         This method is pure: it reads no file, touches no store and
    ///         mutates nothing it is given. That is what makes the guard over it
    ///         honest — see <see cref="SessionLogGuard" />.
    ///     </para>
    /// </remarks>
    /// <param name="log">The log records, in write order.</param>
    /// <returns>The folded surface.</returns>
    public static SessionSurface Fold(IReadOnlyList<SessionLogEntry> log)
    {
        ArgumentNullException.ThrowIfNull(log);

        var warnings = new List<string>();
        var positionsById = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        var lastPositionById = new Dictionary<string, int>(StringComparer.Ordinal);
        var rewindEvents = new List<RewindEvent>();
        var messagePositions = new List<int>();
        int previousPosition = int.MinValue;

        for (int i = 0; i < log.Count; i++)
        {
            SessionLogEntry entry = log[i];

            if (entry.Position <= previousPosition)
            {
                warnings.Add(
                    $"Session log record '{entry.Id}' is out of order (position {entry.Position} after "
                    + $"{previousPosition}); the log was folded in array order.");
            }

            previousPosition = entry.Position;

            if (entry.Kind == SessionLogEntryKind.Rewind)
            {
                rewindEvents.Add(new RewindEvent(entry.Position, entry.AnchorId, entry.Id));
                continue;
            }

            if (entry.Message is null)
            {
                warnings.Add(
                    $"Session log message record '{entry.Id}' carries no message; it is in the log but "
                    + "not in the surface.");
                continue;
            }

            if (!positionsById.TryGetValue(entry.Id, out List<int>? positions))
            {
                positions = new List<int>();
                positionsById[entry.Id] = positions;
            }

            positions.Add(entry.Position);
            lastPositionById[entry.Id] = entry.Position;
            messagePositions.Add(entry.Position);
        }

        // The rewind events are resolved in log order, and each is judged against
        // the surface as it was when the event was written: an event whose
        // anchor an earlier rewind already removed hides nothing (fail-open).
        var hidden = new HashSet<int>();
        var sortedPositions = new List<int>(messagePositions);
        sortedPositions.Sort();

        foreach (RewindEvent rewind in rewindEvents)
        {
            if (string.IsNullOrEmpty(rewind.AnchorId))
            {
                warnings.Add(
                    $"Rewind event '{rewind.EventId}' carries no anchor; it removes nothing.");
                continue;
            }

            if (!TryFindAnchor(positionsById, rewind.AnchorId, rewind.Position, out int anchorPosition))
            {
                warnings.Add(
                    $"Rewind event '{rewind.EventId}' anchors on unknown message '{rewind.AnchorId}'; it "
                    + "removes nothing (fail-open).");
                continue;
            }

            if (hidden.Contains(anchorPosition))
            {
                warnings.Add(
                    $"Rewind event '{rewind.EventId}' anchors on message '{rewind.AnchorId}', which an "
                    + "earlier rewind already removed from the surface; it removes nothing (fail-open).");
                continue;
            }

            AddRange(sortedPositions, anchorPosition, rewind.Position, hidden);
        }

        var entries = new List<SessionLogEntry>(log.Count);
        var current = new List<AgentMessage>();
        var shadowed = new List<SessionLogEntry>();
        var logOnly = new List<SessionLogEntry>();

        for (int i = 0; i < log.Count; i++)
        {
            SessionLogEntry entry = log[i];

            if (entry.Kind == SessionLogEntryKind.Rewind)
            {
                SessionLogEntry classified = entry with { State = SessionLogEntryState.LogOnly };
                entries.Add(classified);
                logOnly.Add(classified);
                continue;
            }

            if (entry.Message is null)
            {
                SessionLogEntry classified = entry with { State = SessionLogEntryState.Shadowed };
                entries.Add(classified);
                shadowed.Add(classified);
                continue;
            }

            bool isCurrent = lastPositionById[entry.Id] == entry.Position
                             && !hidden.Contains(entry.Position);

            SessionLogEntry state = entry with
            {
                State = isCurrent ? SessionLogEntryState.Current : SessionLogEntryState.Shadowed,
            };

            entries.Add(state);

            if (isCurrent)
            {
                current.Add(entry.Message);
            }
            else
            {
                shadowed.Add(state);
            }
        }

        return new SessionSurface
        {
            Entries = entries,
            Current = current,
            Shadowed = shadowed,
            LogOnly = logOnly,
            Warnings = warnings,
        };
    }

    /// <summary>
    ///     The position of the anchor as it was when the rewind was written: the
    ///     latest version of the anchor id that precedes the event.
    /// </summary>
    /// <param name="positionsById">Every message position, grouped by message id.</param>
    /// <param name="anchorId">The anchor message id.</param>
    /// <param name="rewindPosition">The position of the rewind event.</param>
    /// <param name="anchorPosition">The anchor's position, when it was found.</param>
    /// <returns>True when the anchor existed before the event.</returns>
    private static bool TryFindAnchor(
        Dictionary<string, List<int>> positionsById,
        string anchorId,
        int rewindPosition,
        out int anchorPosition)
    {
        anchorPosition = -1;

        if (!positionsById.TryGetValue(anchorId, out List<int>? positions))
        {
            return false;
        }

        foreach (int position in positions)
        {
            if (position < rewindPosition && position > anchorPosition)
            {
                anchorPosition = position;
            }
        }

        return anchorPosition >= 0;
    }

    /// <summary>
    ///     Hide every position strictly between the anchor and the event.
    /// </summary>
    /// <param name="sortedPositions">Every message position, ascending.</param>
    /// <param name="low">The anchor's position (exclusive).</param>
    /// <param name="high">The event's position (exclusive).</param>
    /// <param name="hidden">The set to add to.</param>
    private static void AddRange(
        List<int> sortedPositions,
        int low,
        int high,
        HashSet<int> hidden)
    {
        int start = UpperBound(sortedPositions, low);

        for (int i = start; i < sortedPositions.Count && sortedPositions[i] < high; i++)
        {
            hidden.Add(sortedPositions[i]);
        }
    }

    /// <summary>
    ///     The index of the first value strictly greater than
    ///     <paramref name="value" /> in an ascending list.
    /// </summary>
    /// <param name="sorted">An ascending list.</param>
    /// <param name="value">The value to search past.</param>
    /// <returns>The index of the first greater value, or the list's length.</returns>
    private static int UpperBound(List<int> sorted, int value)
    {
        int low = 0;
        int high = sorted.Count;

        while (low < high)
        {
            int middle = low + ((high - low) / 2);

            if (sorted[middle] <= value)
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }

        return low;
    }

    /// <summary>
    ///     One rewind event, as the fold needs it.
    /// </summary>
    /// <param name="Position">Where the event sits in the log.</param>
    /// <param name="AnchorId">The message the tail was cut at.</param>
    /// <param name="EventId">The event's own id, for the warning messages.</param>
    private readonly record struct RewindEvent(int Position, string? AnchorId, string EventId);
}
