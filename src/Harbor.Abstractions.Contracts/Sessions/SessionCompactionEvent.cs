// SessionCompactionEvent.cs — the durable record of a compaction operation
// (#1767).
//
// WHY A COMPACTION IS A RECORD AND NOT JUST A MESSAGE
// ---------------------------------------------------
// Until now a compaction left ONE trace: the summary message it appended to
// the session. Everything else about the operation — that it ran, which turn
// triggered it, how many messages it folded, how many tokens it saved, and
// above all WHETHER IT FINISHED — was memory and telemetry, and both die with
// the process.
//
// The lock model this file introduces makes the operation a BRACKET in the
// session log:
//
//   compaction/start   the lock is taken. It is appended FIRST, before the
//                      summarizer runs and before anything else is written.
//   compaction/end     the lock is released. It is appended LAST, after the
//                      summary message landed.
//
// Releasing the lock LAST is the whole engineering point: a process that dies
// between the two leaves an UNMATCHED start in the log, and an unmatched start
// is DETECTABLE EVIDENCE that a compaction did not finish. The other order
// would leave an `end` that falsely claims the compaction succeeded.
//
// WHY THE START CARRIES AN OWNER
// ------------------------------
// The owner is the turn the compaction ran in: a number for an automatic
// compaction, null for a standalone one. It says WHO held the lock, so a
// reader of the log can tell two compactions apart, and so an orphaned lock
// names the turn that died.
//
// WHY THE ORPHAN IS CARRIED FORWARD
// ---------------------------------
// `OrphanedTurn` is the turn of the unmatched start a new compaction found in
// the log. The orphan itself is never rewritten (the log is append-only), so
// the evidence of the crash would otherwise disappear under the next
// successful compaction. Carrying it on the next start keeps the trail: the
// log says "this compaction started after an orphaned one from turn N".
//
// THE EVENT IS NOT A MESSAGE
// --------------------------
// A compaction record never enters the model context. It is log-only, exactly
// like the rewind event of #1784: it says what happened to the session, not
// what the model said.

namespace Harbor.Abstractions.Sessions;

/// <summary>
///     What kind of compaction record an event is.
/// </summary>
public enum SessionCompactionEventKind
{
    /// <summary>
    ///     The lock is taken. Appended before the summarizer runs, and before
    ///     anything else the compaction writes.
    /// </summary>
    Start,

    /// <summary>
    ///     The lock is released. Appended after everything the compaction
    ///     wrote, and carries the failure text when the compaction failed.
    /// </summary>
    End,
}

/// <summary>
///     One durable record of a compaction operation (#1767).
/// </summary>
/// <remarks>
///     <para>
///         The record is deliberately dumb: it carries the facts of the
///         operation and no verdict of its own. Whether a lock is HELD is a
///         property of the whole event list, and only
///         <see cref="CompactionLock" /> can decide it.
///     </para>
///     <para>
///         <see cref="Sequence" /> is the record's position in the session log,
///         counting every record the store appended — messages and compaction
///         events alike. It is what lets a reader prove that a write landed
///         INSIDE a compaction's bracket instead of around it.
///     </para>
/// </remarks>
public sealed record SessionCompactionEvent
{
    /// <summary>
    ///     What kind of record this is: the lock being taken, or released.
    /// </summary>
    public required SessionCompactionEventKind Kind { get; init; }

    /// <summary>
    ///     The turn the compaction ran in: a number for an automatic
    ///     compaction, null for a standalone one. This is the lock's OWNER —
    ///     the end record releases the lock the start record took, and the two
    ///     name the same owner.
    /// </summary>
    public int? Turn { get; init; }

    /// <summary>
    ///     The failure text, on an <see cref="SessionCompactionEventKind.End" />
    ///     record of a compaction that FAILED. Null on a successful end and on
    ///     every start.
    /// </summary>
    /// <remarks>
    ///     A failed compaction is a RECORDED fact, not a silent one: the end
    ///     record exists precisely so that "the compaction failed, and here is
    ///     why" survives the process.
    /// </remarks>
    public string? Error { get; init; }

    /// <summary>
    ///     How many messages the compaction folded into the summary. Zero
    ///     until the compaction finished.
    /// </summary>
    public int PrunedMessageCount { get; init; }

    /// <summary>
    ///     How many tokens the compaction is estimated to have saved. Zero
    ///     until the compaction finished.
    /// </summary>
    public int TokensSaved { get; init; }

    /// <summary>
    ///     When the record was written.
    /// </summary>
    public DateTimeOffset OccurredAt { get; init; }

    /// <summary>
    ///     The record's position among the session's COMPACTION records,
    ///     counted from zero in write order.
    /// </summary>
    /// <remarks>
    ///     The position is what lets a reader see that the compaction records
    ///     are in the order they were written — and that a record is missing
    ///     from the middle of the run, the way a rewind event's position says
    ///     which records it covers.
    /// </remarks>
    public int Sequence { get; init; }

    /// <summary>
    ///     The turn of the ORPHANED lock this compaction found in the log: the
    ///     owner of an unmatched start, left behind by a compaction that never
    ///     finished. Null when the log held no orphan.
    /// </summary>
    /// <remarks>
    ///     The orphan does not block the new compaction — a session whose
    ///     previous compaction died must stay usable — but it is never
    ///     swallowed either: the new start names it, so the log keeps the
    ///     evidence of the crash.
    /// </remarks>
    public int? OrphanedTurn { get; init; }
}
