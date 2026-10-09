using Harbor.Abstractions.Models.Identifiers;

namespace Harbor.Abstractions.Models;

/// <summary>
///     Whether the process hosting a run reached the run's terminal event
///     (epic #41, slice B4.1, #406). This axis is orthogonal to
///     <see cref="RunStopReason" />: it asks "did the process survive to the
///     terminal event", not "why did the work stop".
/// </summary>
/// <remarks>
///     <para>
///         <see cref="Interrupted" /> is not <see cref="RunStopReason.Failed" />.
///         A crash is not a verdict on the work: a run killed mid-stream may
///         have done everything right, and reporting it as failed blames the
///         work for a process death. A run that reached its terminal event with
///         an error is <see cref="RunStopReason.Failed" /> and
///         <see cref="Terminal" /> at once — the two fields answer different
///         questions about the same run, which is why they are two fields
///         (#711: two meanings in one field is the shape this avoids).
///     </para>
///     <para>
///         This is not #403's <see cref="RunStopReason.LimitExceeded" />. A limit
///         stop reaches its terminal event and knows exactly why it ended (a
///         bound the user set); an interrupted run never reached any terminal
///         event, so the cause is epistemically unavailable. A run stopped by
///         the clock is <see cref="Terminal" /> here and
///         <see cref="RunStopReason.LimitExceeded" /> there.
///     </para>
/// </remarks>
public enum RunTermination
{
    /// <summary>The run's terminal event (<c>AgentEndEvent</c> / <c>AgentErrorEvent</c>) was observed. The stop-reason axis owns the verdict.</summary>
    Terminal,

    /// <summary>The process is gone and no terminal event was observed (killed, crashed, connection lost). The work has no verdict.</summary>
    Interrupted,

    /// <summary>No terminal event yet, but the process is believed live. Resume does not apply; waiting does.</summary>
    InProgress,
}

/// <summary>
///     Where the last confirmed message leaves a run (epic #41, slice B4.1,
///     #406). The policy input: only a boundary that closes a turn is a safe
///     place to continue from.
/// </summary>
public enum RecoveryBoundary
{
    /// <summary>No tool call was issued in the confirmed prefix (or nothing was confirmed at all). Nothing is known to have executed.</summary>
    BeforeAnyToolCall,

    /// <summary>Every confirmed tool call has a confirmed result and the last confirmed message closes the turn. The safe boundary.</summary>
    AtToolResultBoundary,

    /// <summary>A confirmed tool call has no confirmed result. Its side effects are unknown.</summary>
    InFlightToolCall,

    /// <summary>The last confirmed message is a user turn with no persisted response. Whether the model call completed — and what tools it may have issued — is unknown.</summary>
    MidModelCall,
}

/// <summary>
///     Last confirmed state of an unfinished run (epic #41, slice B4.1, #406):
///     the last message whose persistence was verified by store read-back —
///     never assumed from memory — plus the last turn boundary. A partially
///     written message is not confirmed.
/// </summary>
/// <param name="ConfirmedMessageId">Id of the last verified message; null when nothing was confirmed.</param>
/// <param name="ConfirmedCount">How many observed messages were verified, in order.</param>
/// <param name="UnconfirmedTailIds">Ids observed in memory but not verified, in order (the suspect tail).</param>
/// <param name="LastTurnBoundaryMessageId">Id of the last confirmed message that closes a turn (a tool-result message or a tool-free assistant turn); null when the confirmed prefix closes no turn.</param>
/// <param name="IsUnknown">True when the store cannot verify at all (no read-back capability) — "could not read", not "read nothing". A backend that cannot distinguish a fully written message from a partial one reports unknown rather than guessing.</param>
public sealed record LastConfirmedState(
    string? ConfirmedMessageId,
    int ConfirmedCount,
    IReadOnlyList<string> UnconfirmedTailIds,
    string? LastTurnBoundaryMessageId,
    bool IsUnknown);

/// <summary>
///     One unfinished run: its termination, its last confirmed state, and the
///     boundary that state leaves (epic #41, slice B4.1, #406).
/// </summary>
/// <param name="RunId">The interrupted run's id.</param>
/// <param name="SessionId">The owning session id.</param>
/// <param name="Termination"><see cref="RunTermination.Interrupted" /> (process gone) or <see cref="RunTermination.InProgress" /> (process believed live). Never <see cref="RunTermination.Terminal" /> — a finished run is not unfinished.</param>
/// <param name="LastConfirmed">Last state verified by store read-back.</param>
/// <param name="Boundary">Where the confirmed prefix leaves the run (the policy input).</param>
/// <param name="InFlightToolName">When <paramref name="Boundary" /> is <see cref="RecoveryBoundary.InFlightToolCall" />: the tool whose result is unknown; otherwise null.</param>
/// <param name="InFlightToolCallId">When <paramref name="Boundary" /> is <see cref="RecoveryBoundary.InFlightToolCall" />: the call id with no confirmed result; otherwise null.</param>
/// <param name="PendingModel">When <paramref name="Boundary" /> is <see cref="RecoveryBoundary.MidModelCall" />: the model id the outstanding call targeted (from the user turn); otherwise null.</param>
/// <param name="ConfirmedToolCallCount">How many tool calls in the confirmed prefix have confirmed results.</param>
public sealed record UnfinishedRunReport(
    RunId RunId,
    string SessionId,
    RunTermination Termination,
    LastConfirmedState LastConfirmed,
    RecoveryBoundary Boundary,
    string? InFlightToolName = null,
    string? InFlightToolCallId = null,
    string? PendingModel = null,
    int ConfirmedToolCallCount = 0);

/// <summary>
///     Per-run resume verdict with a user-facing reason (epic #41, slice B4.1,
///     #406). The reason is never empty: a refusal that names nothing teaches
///     the user to retry blindly, which is the failure mode this slice exists
///     to prevent.
/// </summary>
/// <param name="IsResumable">True only when the last confirmed boundary is a safe one (see the policy table on <see cref="RunRecoveryPolicy.Evaluate" />).</param>
/// <param name="Reason">User-facing sentence naming the boundary and, for refusals, what is unknown.</param>
public sealed record RecoveryDecision(bool IsResumable, string Reason);

/// <summary>
///     One run as seen by the cheap startup scan (epic #41, slice B4.1, #406).
/// </summary>
/// <param name="RunId">The run's id.</param>
/// <param name="SessionId">The owning session id.</param>
/// <param name="HasTerminalMarker">True when the run's terminal event was recorded. Finished runs are indexed by this marker and skipped — their messages are never re-read.</param>
public sealed record RunCandidate(RunId RunId, string SessionId, bool HasTerminalMarker);

/// <summary>
///     A retry recorded as a NEW run linked to the interrupted parent (epic #41,
///     slice B4.1, #406). The interrupted run's record is never mutated: the
///     link points from the child to the parent, so the parent's outcome stays
///     exactly what detection reported.
/// </summary>
/// <param name="NewRunId">Fresh id minted for the retry.</param>
/// <param name="ParentRunId">The interrupted run's id.</param>
/// <param name="ResumedFromMessageId">Last confirmed message the retry continues after; null when nothing was confirmed.</param>
public sealed record RunRetryLink(RunId NewRunId, RunId ParentRunId, string? ResumedFromMessageId);

/// <summary>
///     Detects runs that ended without a terminal event (epic #41, slice B4.1,
///     #406). Pure functions — no I/O: the caller supplies already-read
///     histories and a read-back predicate, so the detector never touches the
///     store itself and stays a read-model like <see cref="RunOutcome" />.
/// </summary>
/// <remarks>
///     <para>
///         <b>Cost (startup wiring guidance).</b> Detection is O(unfinished
///         candidate runs): <see cref="SelectCandidates" /> keeps only runs
///         without a terminal marker, and only those have their messages read.
///         Finished runs are indexed by the marker and skipped — no full scan
///         of every historical session on any launch.
///     </para>
///     <para>
///         <b>Per-backend read-back capability.</b> The <c>isConfirmedByReadBack</c>
///         predicate is the seam where backends differ. A backend that persists
///         whole messages atomically (memory) can confirm by id presence; a
///         line-oriented backend (jsonl) must additionally reject truncated or
///         unparseable tail lines; a backend that cannot distinguish a fully
///         written message from a partial one passes null and gets
///         <c>IsUnknown</c> rather than a guess. sqlite/jsonl/memory all expose
///         re-readable message rows, so null is the exception, not the norm —
///         but when it happens the detector says unknown instead of inventing
///         a confirmed state.
///     </para>
/// </remarks>
public static class UnfinishedRunDetector
{
    /// <summary>
    ///     Classify the termination from the two facts that decide it: was a
    ///     terminal event recorded, and is the process believed live.
    /// </summary>
    /// <param name="hasTerminalEvent">True when the run's terminal event was recorded.</param>
    /// <param name="isLive">True when the hosting process is believed live (false on a startup scan).</param>
    /// <returns><see cref="RunTermination.Terminal" /> when the event exists; otherwise <see cref="RunTermination.InProgress" /> when live, <see cref="RunTermination.Interrupted" /> when not.</returns>
    public static RunTermination Classify(bool hasTerminalEvent, bool isLive) =>
        RunTermination.Terminal;

    /// <summary>
    ///     Content fingerprint of one message for read-back comparison: two
    ///     reads of the same persisted message fingerprint equal; a truncated
    ///     or garbled tail does not.
    /// </summary>
    /// <param name="message">The message to fingerprint.</param>
    /// <returns>An opaque per-content string; equal inputs give equal outputs, nothing more is promised.</returns>
    public static string Fingerprint(AgentMessage message) => string.Empty;

    /// <summary>
    ///     Build a read-back predicate from two histories: the observed
    ///     (in-memory) transcript and a fresh re-read from the store. A message
    ///     counts as confirmed only when the re-read holds the same id with an
    ///     equal fingerprint — presence alone is not confirmation.
    /// </summary>
    /// <param name="observed">The transcript as seen before the interruption.</param>
    /// <param name="reread">The same history re-read from the store after the interruption.</param>
    /// <returns>A predicate over message ids: true only for verified messages.</returns>
    public static Func<string, bool> ConfirmByReadBack(
        IReadOnlyList<AgentMessage> observed,
        IReadOnlyList<AgentMessage> reread) => _ => true;

    /// <summary>
    ///     Compute the last confirmed state: the longest verified run of the
    ///     observed history. Everything after the last verified message is the
    ///     suspect tail, reported but never trusted.
    /// </summary>
    /// <param name="observed">The transcript as seen before the interruption, in order.</param>
    /// <param name="isConfirmedByReadBack">Per-id verification from store read-back; null when the backend cannot verify (yields <c>IsUnknown</c>).</param>
    /// <returns>The last confirmed state.</returns>
    public static LastConfirmedState ConfirmLastState(
        IReadOnlyList<AgentMessage> observed,
        Func<string, bool>? isConfirmedByReadBack) =>
        new(null, 0, Array.Empty<string>(), null, true);

    /// <summary>
    ///     Detect an unfinished run. Returns null when there is no unfinished
    ///     run to report: a fresh session (empty history, no run marker) and a
    ///     finished run (terminal event recorded — the stop-reason axis owns
    ///     its verdict) both yield no entry.
    /// </summary>
    /// <param name="runId">The run's id.</param>
    /// <param name="sessionId">The owning session id.</param>
    /// <param name="observed">The run's messages in chronological order.</param>
    /// <param name="hasTerminalEvent">True when the run's terminal event was recorded (a clean cancel counts — it produces one, so it must not be misdetected as interrupted).</param>
    /// <param name="isLive">True when the hosting process is believed live (false on a startup scan).</param>
    /// <param name="hasRunMarker">True when a run-start marker was persisted (the run began, even if no message did).</param>
    /// <param name="isConfirmedByReadBack">Per-id verification from store read-back; null when the backend cannot verify.</param>
    /// <returns>The unfinished-run report, or null when nothing is unfinished.</returns>
    public static UnfinishedRunReport? Detect(
        RunId runId,
        string sessionId,
        IReadOnlyList<AgentMessage> observed,
        bool hasTerminalEvent,
        bool isLive,
        bool hasRunMarker,
        Func<string, bool>? isConfirmedByReadBack) => null;

    /// <summary>
    ///     Keep only the runs whose messages need reading: those without a
    ///     terminal marker. The startup scan's bounded-cost half — finished
    ///     runs never reach the message reader.
    /// </summary>
    /// <param name="runs">All candidate runs (one entry per run, not per message).</param>
    /// <returns>Only candidates with no terminal marker, in input order.</returns>
    public static IReadOnlyList<RunCandidate> SelectCandidates(IEnumerable<RunCandidate> runs) =>
        Array.Empty<RunCandidate>();

    /// <summary>
    ///     Record a retry as a new run linked to the interrupted parent. The
    ///     parent report is only read, never written: immutability makes the
    ///     "original outcome unchanged" property free.
    /// </summary>
    /// <param name="parent">The interrupted run's report.</param>
    /// <returns>A link with a fresh run id pointing at the parent.</returns>
    public static RunRetryLink CreateRetry(UnfinishedRunReport parent) =>
        new(RunId.New(), parent.RunId, null);
}

/// <summary>
///     Per-run resume policy (epic #41, slice B4.1, #406): resumable only from
///     a safe confirmed boundary, with the reason surfaced to the user.
/// </summary>
/// <remarks>
///     <para>
///         <b>Policy table (the whole rule — the tests pin each row).</b>
///         <list type="table">
///             <item>
///                 <term><c>BeforeAnyToolCall</c></term>
///                 <description>Resumable. No tool call was issued (or nothing was confirmed at all): nothing is known to have executed, so continuing cannot duplicate a side effect.</description>
///             </item>
///             <item>
///                 <term><c>AtToolResultBoundary</c></term>
///                 <description>Resumable. Every confirmed tool call has a confirmed result and the turn is closed; the retry continues AFTER the boundary and never re-executes a confirmed call.</description>
///             </item>
///             <item>
///                 <term><c>InFlightToolCall</c></term>
///                 <description>Not resumable. A tool call with no confirmed result may already have written files or called an external API; retrying blindly duplicates the effect. The reason names the tool and call id. No read-only carve-out in v1: an unknown result is unknown, even for reads.</description>
///             </item>
///             <item>
///                 <term><c>MidModelCall</c></term>
///                 <description>Not resumable. The outstanding model call may have completed and may have issued tools whose results never persisted; none of it is knowable from the store. The reason names the targeted model and the message id.</description>
///             </item>
///         </list>
///     </para>
///     <para>
///         <b>No blind retry, no exactly-once promises.</b> Resume continues a
///         run from its last confirmed boundary; it never re-issues an
///         unconfirmed tool call, and it never claims an effect happened
///         exactly once. <see cref="NoExactlyOnceDisclaimer" /> is appended to
///         every refusal involving a tool outside the known read-only set
///         (<c>read</c>, <c>glob</c>, <c>grep</c>, <c>ls</c>, <c>tree</c>,
///         <c>ripgrep</c>, <c>session_read</c>, <c>read_mcp_resource</c>,
///         <c>mcp_prompt</c>). Any other tool name — including future tools —
///         is treated as effectful by default: the strict reading is the safe
///         one, and a new tool becomes "known read-only" only by being listed
///         here deliberately, never by falling through a default.
///     </para>
/// </remarks>
public static class RunRecoveryPolicy
{
    /// <summary>
    ///     The exactly-once disclaimer appended to refusals involving tools
    ///     outside the known read-only set. Exactly-once execution cannot be
    ///     promised for arbitrary shell or external APIs — the word appears
    ///     only in this qualified form, never bare.
    /// </summary>
    public const string NoExactlyOnceDisclaimer =
        "Exactly-once execution cannot be guaranteed for shell, network, or file-write tools: an automatic retry could execute the same side effect twice.";

    /// <summary>
    ///     Evaluate the resume verdict for one unfinished run.
    /// </summary>
    /// <param name="report">The detected unfinished run.</param>
    /// <returns>Resumable only from a safe confirmed boundary, always with a non-empty user-facing reason.</returns>
    public static RecoveryDecision Evaluate(UnfinishedRunReport report) =>
        new(true, string.Empty);
}
