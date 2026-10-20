// SessionSnapshot.cs — the ONE-GENERATION view of a session (#1785).
//
// WHY THIS EXISTS
// ---------------
// The export used to read a JSONL session THREE times, in three separate store
// calls:
//
//   1. GetAsync        — the session record: the line-1 header PLUS the stats
//                        derived from the history (the store folds them itself).
//   2. GetStatsAsync   — the stats, again, as an independent derivation.
//   3. GetMessagesAsync— the history the export writes as its body.
//
// Each call is a separate read of a file that a live agent is still writing.
// A write landing between them moves the file to a new generation, and the
// export then assembles parts of TWO generations: the stats of the first
// generation next to the history of the second. An export is a "send me the
// log" artefact — a log whose header counts disagree with its body is worse
// than no log, because it misleads whoever reads it.
//
// So the export is now assembled from ONE snapshot: the header, the history and
// the stats, all taken from a SINGLE pass over the file. The snapshot is a
// coherent view of the session at some instant T — which instant is not
// promised, and does not need to be: what is promised is that every part of
// the export describes the SAME instant.
//
// The three parts were never three different intentions — the issue's guess
// ("metadata / messages / checkpoints") was wrong on the third: NOTHING reads
// checkpoints here. Pass 1 is the header (plus the stats the store derives
// while reading it), pass 2 is those same stats read a second time, pass 3 is
// the history. Pass 2 was pure duplication: every store returns the same
// metadata from GetAsync and from GetStatsAsync, so the second call bought
// one extra read and one extra chance to disagree with the first.

namespace Harbor.Storage.Jsonl;

/// <summary>
///     One coherent view of a session: the session record and the message
///     history, both taken from a SINGLE read of the underlying state.
/// </summary>
/// <param name="Session">
///     The session record — the header plus the stats derived from the very
///     history this snapshot carries.
/// </param>
/// <param name="Messages">The message history, in chronological order.</param>
internal sealed record SessionSnapshot(
    Session Session,
    IReadOnlyList<AgentMessage> Messages);
