// TerminalActivityClassifier.cs — #2680 F125. Reduces the observable state of
// one session to a terminal-activity state, then formats that state as the
// short indicator a tab title or agent label paints. A pure, BCL-only leaf: it
// starts nothing, reads no clock and imports no Harbor namespace, so it stays
// inside the CellForge.Engine atomicity perimeter (zero project references,
// zero Harbor imports). The engine owns the vocabulary; the renderer that
// drives it decides which signals to feed in.
//
// The upstream shape this ports (kilocode/cli/cmd/tui/terminal-activity.ts)
// has a State, a classify() and a format(). This file is that triad over a
// small signal struct rather than over Harbor's SessionStatsEvent: the engine
// is deliberately unaware of agents and events, so the caller projects its
// state into TerminalActivitySignals and Classify applies one precedence
// table. Keeping the boundary here means the same table grades a single
// session and the aggregate of a family of sessions.

namespace Harbor.Tui.CellForge.Rendering;

/// <summary>
///     The coarse activity state of a session, used for a terminal title,
///     tab label or status glyph (#2680 F125). Ordered from quiet to loud so
///     a consumer can rank sessions by urgency with a simple numeric compare.
/// </summary>
public enum TerminalActivityState : byte
{
    /// <summary>No work in flight and nothing awaiting the user.</summary>
    Idle = 0,

    /// <summary>The agent is processing a turn or running a tool.</summary>
    Busy = 1,

    /// <summary>A failed step is being retried with backoff.</summary>
    Retrying = 2,

    /// <summary>The session is blocked on user input, a permission or a question.</summary>
    Waiting = 3,

    /// <summary>The last step ended in an error that needs attention.</summary>
    Error = 4,

    /// <summary>The session finished its work and is no longer active.</summary>
    Done = 5,
}

/// <summary>
///     The signals a caller feeds into <see cref="TerminalActivityClassifier" />.
///     Each flag is independent; <see cref="Classify" /> resolves simultaneous
///     flags with a fixed precedence rather than trusting the caller to pick
///     one. A <see langword="default" /> value means "nothing is happening".
/// </summary>
public readonly record struct TerminalActivitySignals
{
    /// <summary>A turn or tool call is in flight.</summary>
    public bool IsBusy { get; init; }

    /// <summary>A transient failure is being retried.</summary>
    public bool IsRetrying { get; init; }

    /// <summary>The session waits for a permission, an answer or other user action.</summary>
    public bool IsWaitingForUser { get; init; }

    /// <summary>An unrecovered error is the most recent outcome.</summary>
    public bool HasError { get; init; }

    /// <summary>The session reached a terminal, finished state.</summary>
    public bool IsFinished { get; init; }

    /// <summary>An all-clear signal set: no work, no wait, no error.</summary>
    public static TerminalActivitySignals None => default;
}

/// <summary>
///     Maps <see cref="TerminalActivitySignals" /> to a
///     <see cref="TerminalActivityState" /> and formats that state as a short
///     indicator (#2680 F125). Pure and stateless: the same signals always
///     yield the same state and the same text, with no culture-sensitive
///     formatting.
/// </summary>
public static class TerminalActivityClassifier
{
    /// <summary>
    ///     Resolves the signals to a single state. Precedence is loudest-first:
    ///     an error outranks a wait, a wait outranks a retry, a retry outranks
    ///     ordinary work, and work outranks a finished session. If no flag is
    ///     set the session is <see cref="TerminalActivityState.Idle" />.
    /// </summary>
    /// <param name="signals">The caller's view of the session.</param>
    public static TerminalActivityState Classify(TerminalActivitySignals signals)
    {
        if (signals.HasError) return TerminalActivityState.Error;
        if (signals.IsWaitingForUser) return TerminalActivityState.Waiting;
        if (signals.IsRetrying) return TerminalActivityState.Retrying;
        if (signals.IsBusy) return TerminalActivityState.Busy;
        if (signals.IsFinished) return TerminalActivityState.Done;
        return TerminalActivityState.Idle;
    }

    /// <summary>
    ///     Returns the compact indicator for <paramref name="state" />. The
    ///     text is fixed and ordinal; it is meant to be concatenated into a
    ///     tab title by the caller, not parsed back into a state.
    /// </summary>
    /// <param name="state">The state to render.</param>
    public static string Format(TerminalActivityState state) => state switch
    {
        TerminalActivityState.Busy => "● busy",
        TerminalActivityState.Retrying => "↻ retrying",
        TerminalActivityState.Waiting => "? attention",
        TerminalActivityState.Error => "✖ error",
        TerminalActivityState.Done => "✓ done",
        _ => "○ idle",
    };
}
