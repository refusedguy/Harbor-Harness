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
//      message whose PLACE IN THE CONVERSATION lies in (pos(A), R). Records
//      written AFTER the event are untouched — that is what makes "rewind and
//      carry on" work: a message written after the rewind is current again.
//   3. LOG-ONLY. A record that was never a message (a rewind event) is
//      log-only. It is in the log and in no surface, ever.
//
// THE PLACE OF A MESSAGE IS WHERE IT WAS WRITTEN FIRST
// ---------------------------------------------------
// A message that is edited keeps the place it was written at: the edit is a
// NEW VERSION appended to the log, and the conversation still shows the
// message where it was said. So a message's place is the position of its
// FIRST record, and the surface is ordered by that place — the same rule the
// JSONL reader has always applied to the messages it hands back ("latest id
// wins, the first-occurrence position counts"). The fold and the reader must
// not disagree about where a message sits, or the same session would read
// differently depending on which of the two produced it.
//
// It also settles what a rewind to an EDITED anchor means. The anchor's place
// is where it was first written, so the cut is made there: everything said
// after that place leaves the surface, and the anchor itself — in its current
// version — stays.
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
    ///     the log still shows, in the order the conversation had them.
    /// </summary>
    /// <remarks>
    ///     A message keeps the place it was written at even when a later
    ///     version of it was appended: the edit is a new record, not a move.
    ///     The list is chronological when the log was written out of time
    ///     order — the same rule the JSONL reader applies.
    /// </remarks>
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
        var firstPositionById = new Dictionary<string, int>(StringComparer.Ordinal);
        var lastPositionById = new Dictionary<string, int>(StringComparer.Ordinal);
        var rewindEvents = new List<RewindEvent>();
        var messageIds = new List<string>();
        var seenIds = new HashSet<string>(StringComparer.Ordinal);
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

            // The place of a message is where it was written FIRST; the version
            // that is current is the one written LAST.
            if (seenIds.Add(entry.Id))
            {
                messageIds.Add(entry.Id);
                firstPositionById[entry.Id] = entry.Position;
            }

            lastPositionById[entry.Id] = entry.Position;
        }

        // The rewind events are resolved in log order, and each is judged against
        // the surface as it was when the event was written: an event whose
        // anchor an earlier rewind already removed hides nothing (fail-open).
        //
        // The hidden set holds the PLACES of the messages that left the
        // surface — the position of each message's first record. A version
        // appended after a message was cut out does not bring the message
        // back: the message is out of the surface as a whole.
        var hidden = new HashSet<int>();
        var firstPositions = new List<int>(messageIds.Count);
        for (int i = 0; i < messageIds.Count; i++)
        {
            firstPositions.Add(firstPositionById[messageIds[i]]);
        }

        firstPositions.Sort();

        foreach (RewindEvent rewind in rewindEvents)
        {
            if (string.IsNullOrEmpty(rewind.AnchorId))
            {
                warnings.Add(
                    $"Rewind event '{rewind.EventId}' carries no anchor; it removes nothing.");
                continue;
            }

            if (!firstPositionById.TryGetValue(rewind.AnchorId, out int anchorPosition))
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

            AddRange(firstPositions, anchorPosition, rewind.Position, hidden);
        }

        var entries = new List<SessionLogEntry>(log.Count);
        var shadowed = new List<SessionLogEntry>();
        var logOnly = new List<SessionLogEntry>();
        var currentById = new Dictionary<string, AgentMessage>(StringComparer.Ordinal);

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

            AgentMessage? message = entry.Message;

            if (message is null)
            {
                SessionLogEntry classified = entry with { State = SessionLogEntryState.Shadowed };
                entries.Add(classified);
                shadowed.Add(classified);
                continue;
            }

            bool isCurrent = lastPositionById[entry.Id] == entry.Position
                             && !hidden.Contains(firstPositionById[entry.Id]);

            SessionLogEntry state = entry with
            {
                State = isCurrent ? SessionLogEntryState.Current : SessionLogEntryState.Shadowed,
            };

            entries.Add(state);

            if (isCurrent)
            {
                currentById[entry.Id] = message;
            }
            else
            {
                shadowed.Add(state);
            }
        }

        // The surface keeps the order the conversation had: a message keeps the
        // place it was written at, even when a later version of it was appended.
        var current = new List<AgentMessage>(messageIds.Count);
        for (int i = 0; i < messageIds.Count; i++)
        {
            if (currentById.TryGetValue(messageIds[i], out AgentMessage? found) && found is not null)
            {
                current.Add(found);
            }
        }

        return new SessionSurface
        {
            Entries = entries,
            Current = SortChronologically(current),
            Shadowed = shadowed,
            LogOnly = logOnly,
            Warnings = warnings,
        };
    }

    /// <summary>
    ///     The surface's order: the order the messages were written — unless the
    ///     log was written out of time order, in which case the chronological
    ///     order wins.
    /// </summary>
    /// <remarks>
    ///     This is the same rule the JSONL reader has always applied to the
    ///     messages it returns, and it has to stay the same: a session written
    ///     out of time order must read the same way whether the reader or the
    ///     fold produced the list. The sort is stable, so two messages with the
    ///     same instant keep the order they were written in.
    /// </remarks>
    /// <param name="messages">The surface's messages, in write order.</param>
    /// <returns>The same list, or its chronological order.</returns>
    private static List<AgentMessage> SortChronologically(List<AgentMessage> messages)
    {
        for (int i = 1; i < messages.Count; i++)
        {
            if (messages[i - 1].CreatedAt > messages[i].CreatedAt)
            {
                return messages.OrderBy(m => m.CreatedAt).ToList();
            }
        }

        return messages;
    }

    /// <summary>
    ///     Hide every place strictly between the anchor and the event.
    /// </summary>
    /// <param name="sortedPositions">Every message's place, ascending.</param>
    /// <param name="low">The anchor's place (exclusive).</param>
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
