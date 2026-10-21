// CompactionLock.cs — the fold that says whether a compaction lock is held
// (#1767).
//
// WHAT THE FOLD ANSWERS
// ---------------------
// One question: given the compaction records of a session, in write order, is
// a compaction lock HELD right now?
//
// The answer is the last START that no END has released:
//
//   events:   [start(t1), end(t1)]        -> free
//   events:   [start(t1)]                 -> HELD, owner t1  (the crash case)
//   events:   [start(t1), start(t2), end(t2)] -> free
//   events:   [start(t1), end(t1), start(t2)] -> HELD, owner t2
//
// WHY A NEW START SUPERSEDES AN OLD ONE
// --------------------------------------
// The third line above is the interesting one. A start that is followed by
// another start was ABANDONED: its owner never released it, and the only way
// a second lock can be taken while the first is open is that the first owner
// is gone. So the newer start takes the lock over, and the older one is an
// ORPHAN — evidence, not a lock. The fold therefore keeps only the most
// recent open start, and the caller reports the orphan it superseded through
// `SessionCompactionEvent.OrphanedTurn`.
//
// WHY IT IS PURE
// --------------
// The fold reads no file, opens no store and has no side effect: it takes the
// records and answers a question about them. That is what makes it testable
// without a disk, and it is the same shape as `SessionLogFold` (#1784) and
// `TranscriptInspector` (#1744) — the pure inspection lives in the contract
// layer, and every backend answers the same question the same way.

namespace Harbor.Abstractions.Sessions;

/// <summary>
///     The pure inspection of a session's compaction records: which lock, if
///     any, is still held (#1767).
/// </summary>
public static class CompactionLock
{
    /// <summary>
    ///     The start record whose lock no end record has released: the lock the
    ///     session is under RIGHT NOW, or null when no compaction lock is
    ///     held.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         A returned start with no matching end is the ORPHANED LOCK —
    ///         the free signal that a compaction did not finish. It is
    ///         returned as it is, with its owner, so the caller can name the
    ///         turn that died instead of reporting a generic "something went
    ///         wrong".
    ///     </para>
    ///     <para>
    ///         The fold does not decide whether an unmatched start is LIVE
    ///         (a compaction running right now, in this process) or ORPHANED
    ///         (a compaction that died). Only the runtime knows that: the
    ///         in-process guard says whether a compaction is in flight, and
    ///         the difference between the two is what separates "wait" from
    ///         "this session carries the evidence of a crash".
    ///     </para>
    /// </remarks>
    /// <param name="events">The session's compaction records, in write order.</param>
    /// <returns>The open start record, or null when no lock is held.</returns>
    public static SessionCompactionEvent? FindOpenStart(IReadOnlyList<SessionCompactionEvent> events)
    {
        ArgumentNullException.ThrowIfNull(events);

        SessionCompactionEvent? open = null;

        for (int i = 0; i < events.Count; i++)
        {
            SessionCompactionEvent compactionEvent = events[i];

            switch (compactionEvent.Kind)
            {
                case SessionCompactionEventKind.Start:
                    // A start while a lock is open supersedes it: the older
                    // owner never released it, so it is an orphan and not a
                    // lock (see the header).
                    open = compactionEvent;
                    break;

                case SessionCompactionEventKind.End:
                    // The end releases the lock that is open. An end with no
                    // open start releases nothing — a compaction whose start
                    // record was lost still ends cleanly.
                    open = null;
                    break;
            }
        }

        return open;
    }
}
