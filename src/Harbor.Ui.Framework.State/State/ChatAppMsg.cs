using System.Collections.Immutable;
using Harbor.Abstractions.Events;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Models.Identifiers;
namespace Harbor.Ui.Framework.State;

/// <summary>
///     The Harbor chat half of the message union (#33/T4, #364): agent events,
///     transcript appends, session hydration and runtime chrome. Extends
///     <see cref="AppMsg" /> so a <see cref="ChatAppMsg" /> can be dispatched on
///     the same <see cref="UiStore.Dispatch(AppMsg)" /> path as a generic message,
///     and so <see cref="ChatAppReducer" /> can claim exactly these arms through
///     the <see cref="IAppReducerPlugin" /> hook without forking
///     <see cref="AppReducer" />.
/// </summary>
public abstract record ChatAppMsg : AppMsg
{
    /// <summary>Wrap an agent-driven event into the UI pipeline (existing data path).</summary>
    /// <param name="Event">The agent event to reduce.</param>
    public sealed record Agent(AgentEvent Event) : ChatAppMsg;

    /// <summary>
    ///     Effect-host bookkeeping: a prompt run is starting (the loop's own
    ///     <see cref="AgentStartEvent" /> may not have arrived yet). Marks the store
    ///     running so user input is suppressed during the run.
    /// </summary>
    public sealed record AgentStarted : ChatAppMsg;

    /// <summary>
    ///     Effect-host bookkeeping: the prompt run ended. <paramref name="Status" />
    ///     overrides the status bar explicitly; when null, an existing
    ///     <c>"error"</c> status is preserved and everything else falls back to
    ///     <c>"idle"</c>. <paramref name="Error" /> appends an error transcript line.
    /// </summary>
    public sealed record AgentEnded(string? Status = null, string? Error = null) : ChatAppMsg;

    /// <summary>Direct status-bar text update (e.g. <c>"idle"</c> after an abort).</summary>
    /// <param name="Status">The new status-bar text.</param>
    public sealed record StatusChanged(string Status) : ChatAppMsg;

    /// <summary>
    ///     Atomic session hydration (#89): reset to a fresh state, bind the
    ///     session chrome (model / provider / agent) and replay the persisted
    ///     history lines in a SINGLE reducer transition. Replaces the old
    ///     Reset + BindSession + N×AppendLine sequence, whose N+2 separate
    ///     store transitions let a background agent event interleave
    ///     mid-replay and corrupt the transcript order. With this message a
    ///     racing event serializes strictly before or after the swap via the
    ///     store's CAS loop.
    /// </summary>
    /// <param name="Model">The session's model id.</param>
    /// <param name="Provider">The session's provider id.</param>
    /// <param name="AgentName">The session's agent name.</param>
    /// <param name="Lines">The replayed history lines, oldest first.</param>
    /// <param name="Status">
    ///     The status the session already carries — <c>Session.Status</c>, as
    ///     persisted by the core. Switching to a session reads the core's
    ///     answer instead of re-deriving one from the replayed transcript,
    ///     which is the same #687 defect on the switch path: a sub-agent run
    ///     that <c>SubAgentRunner</c> stamped <c>Done</c> comes back as
    ///     <c>Idle</c> if the UI asks what the last replayed line was.
    /// </param>
    public sealed record HydrateSession(
        string Model,
        string Provider,
        string AgentName,
        ImmutableArray<ChatLine> Lines,
        SessionStatus Status = SessionStatus.Idle) : ChatAppMsg;

    /// <summary>
    ///     Runtime identity for the status chrome (model / provider / agent).
    ///     The store never learns these from <see cref="AgentEvent" /> traffic,
    ///     so hosts that bypass the onboarding seed push them explicitly —
    ///     e.g. the CellForge REPL on startup and after model/agent switches.
    /// </summary>
    public sealed record ConfigureRuntime(string Model, string Provider, string AgentName) : ChatAppMsg;

    /// <summary>
    ///     Host-side transcript line (slash handler errors, session-switch notes).
    ///     The TEA replacement for ad-hoc <c>Transition(s =&gt; s.AddLine(...))</c> folds.
    /// </summary>
    public sealed record AppendLine(ChatRole Role, string Text, string? ToolCallId = null) : ChatAppMsg;

    /// <summary>
    ///     Host-side session list sync (CellForge REPL): recent sessions with the
    ///     active mark, so the session-sidebar panel renders from the store
    ///     instead of staying empty. Dispatched on startup/switch/new/fork.
    /// </summary>
    public sealed record SyncSessions(
        ImmutableArray<SessionInfo> Sessions,
        SessionId? ActiveSessionId) : ChatAppMsg;

    /// <summary>
    ///     Host-side diagnostics sync (issue #674): the whole classified
    ///     snapshot from the headless core, language-server rows and tool-output
    ///     rows both tagged by <see cref="DiagnosticIssue.Source" />.
    /// </summary>
    /// <remarks>
    ///     Same shape as <see cref="SyncSessions" /> and for the same reason: the
    ///     core counts, the host pushes, the store holds, the renderer draws. The
    ///     message carries no instruction — it replaces the snapshot outright, so
    ///     a renderer can never hold a half-updated mix of old and new rows.
    /// </remarks>
    /// <param name="Diagnostics">The classified snapshot, exactly as the core composed it.</param>
    public sealed record SyncDiagnostics(ImmutableArray<DiagnosticIssue> Diagnostics) : ChatAppMsg;

    // ── tab strip (#388, slice 1/3 — state + transitions only, no renderer) ──

    /// <summary>
    ///     Open a tab for a session, at the right end of the strip.
    ///     <b>Idempotent:</b> when the session already has a tab this activates
    ///     it instead of appending a duplicate, and leaves its order and fields
    ///     alone (metadata refresh is a follow-up, not an open).
    /// </summary>
    /// <param name="Tab">The descriptor to append — ignored when the session is already open.</param>
    public sealed record OpenTab(SessionTab Tab) : ChatAppMsg;

    /// <summary>
    ///     Focus an open tab and ask the host to switch to its session
    ///     (emits <see cref="TuiEffect.ActivateSession" />). Never reorders.
    /// </summary>
    /// <param name="SessionId">The tab's session id.</param>
    public sealed record ActivateTab(SessionId SessionId) : ChatAppMsg;

    /// <summary>
    ///     Close a tab. When the active tab closes, focus moves to a
    ///     deterministic neighbour — next, else previous, else none.
    /// </summary>
    /// <param name="SessionId">The tab's session id.</param>
    public sealed record CloseTab(SessionId SessionId) : ChatAppMsg;

    /// <summary>
    ///     Close every tab except <paramref name="Keep" /> (the context-menu
    ///     "close others"), then focus the survivor.
    /// </summary>
    /// <param name="Keep">The tab that stays open and becomes active.</param>
    public sealed record CloseOtherTabs(SessionId Keep) : ChatAppMsg;

    /// <summary>
    ///     Close every tab to the right of <paramref name="From" /> ("close
    ///     right"), then focus <paramref name="From" />. Strictly to the right:
    ///     <paramref name="From" /> itself always survives.
    /// </summary>
    /// <param name="From">The leftmost tab that survives.</param>
    public sealed record CloseTabsToRight(SessionId From) : ChatAppMsg;

    /// <summary>
    ///     Pin or unpin a tab. Flag only — pinning never reorders, so the tab
    ///     the user is looking at cannot jump.
    /// </summary>
    /// <param name="SessionId">The tab's session id.</param>
    /// <param name="Pinned">Target pin state.</param>
    public sealed record PinTab(SessionId SessionId, bool Pinned) : ChatAppMsg;

    /// <summary>
    ///     Move a tab to <paramref name="ToIndex" /> in tab order (drag-reorder /
    ///     move-to-index), clamped to the current range. The active tab never
    ///     changes: reordering is presentation, not focus.
    /// </summary>
    /// <param name="SessionId">The tab to move.</param>
    /// <param name="ToIndex">Target index, clamped to <c>[0 .. Tabs.Length - 1]</c>.</param>
    public sealed record ReorderTab(SessionId SessionId, int ToIndex) : ChatAppMsg;

    /// <summary>Focus the next tab in tab order (wraps; no-op with fewer than two tabs).</summary>
    public sealed record CycleNextTab : ChatAppMsg;

    /// <summary>Focus the previous tab in tab order (wraps; no-op with fewer than two tabs).</summary>
    public sealed record CyclePreviousTab : ChatAppMsg;

    // ── tab reopen + unread model (#1173, opencode steal — stack + markers) ──

    /// <summary>
    ///     Restore the most recently closed tab at its original position
    ///     (<c>Ctrl+Shift+T</c>). Entries whose session is already open are
    ///     consumed and skipped, so repeated reopens walk the stack. Empty
    ///     stack (or nothing restorable) → no-op, no revision bump.
    /// </summary>
    public sealed record ReopenTab : ChatAppMsg;

    /// <summary>Focus the next tab carrying an unread signal (wraps; no-op when no other tab is unread).</summary>
    public sealed record CycleNextUnreadTab : ChatAppMsg;

    /// <summary>Focus the previous tab carrying an unread signal (wraps; no-op when no other tab is unread).</summary>
    public sealed record CyclePreviousUnreadTab : ChatAppMsg;

    /// <summary>
    ///     Focus the Nth tab in tab order (<c>Ctrl+1</c>..<c>Ctrl+9</c>).
    ///     Out-of-range slots are a no-op.
    /// </summary>
    /// <param name="Slot">One-based slot (<c>1</c> is the leftmost tab).</param>
    public sealed record ActivateTabSlot(int Slot) : ChatAppMsg;

    /// <summary>
    ///     Mark a background tab unread so the strip shows the marker. The host
    ///     dispatches this when a non-visible session finishes a turn
    ///     (<paramref name="IsError" /> when the run failed — the error colour,
    ///     not amber). Marking the <i>active</i> tab is a no-op: viewing is
    ///     reading, and a marker on the tab on screen would be a lie.
    /// </summary>
    /// <param name="SessionId">The background tab's session id.</param>
    /// <param name="IsError">Whether the unread signal is a failure.</param>
    public sealed record MarkTabUnread(SessionId SessionId, bool IsError = false) : ChatAppMsg;

    /// <summary>Clear a tab's unread signal without focusing it.</summary>
    /// <param name="SessionId">The tab's session id.</param>
    public sealed record MarkTabRead(SessionId SessionId) : ChatAppMsg;

    /// <summary>
    ///     Restore the open-tab order + active tab from a persisted
    ///     <see cref="TabStripSnapshot" /> (#390, slice 3/3).
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The host resolved <paramref name="Tabs" /> against the session
    ///         store before dispatching: only sessions that still exist are
    ///         listed. Snapshot ids with no descriptor are dropped with a single
    ///         system note in the transcript; known descriptors the snapshot
    ///         does not order are appended at the end in the given order, so a
    ///         session the store knows is never lost to a stale payload.
    ///     </para>
    ///     <para>
    ///         Degenerate input restores to an empty strip without crashing —
    ///         never to a invented tab: the reducer cannot create sessions, so
    ///         the host opens the default tab through the existing open path
    ///         when the strip comes back empty. Like
    ///         <see cref="HydrateSession" /> this emits no switch effect; the
    ///         host owns session switching (and schedules the dispatch after
    ///         first paint, so a slow store never blocks startup).
    ///     </para>
    /// </remarks>
    /// <param name="Snapshot">Persisted order + focus, as session-id strings.</param>
    /// <param name="Tabs">Descriptors for the sessions that still exist.</param>
    public sealed record HydrateTabStrip(TabStripSnapshot Snapshot, ImmutableArray<SessionTab> Tabs) : ChatAppMsg;

    // ── screenshot-markup overlay (#400, slice 1/2 — state + transitions; the
    // CellForge overlay paints this snapshot, the host owns open scroll
    // restore, file I/O and the bake) ──

    /// <summary>
    ///     Open the markup overlay over an image row. Snapshots the feed scroll
    ///     (<paramref name="ScrollOffset" />) so close restores it exactly.
    /// </summary>
    public sealed record OpenMarkup(
        string SourcePath,
        string SourceName,
        int SourceWidth,
        int SourceHeight,
        int ScrollOffset) : ChatAppMsg;

    /// <summary>Close the overlay, discarding the session. The host restores the snapshotted scroll.</summary>
    public sealed record CloseMarkup : ChatAppMsg;

    /// <summary>Switch the placement tool (arrow / rectangle / text).</summary>
    public sealed record MarkupSelectTool(MarkupKind Tool) : ChatAppMsg;

    /// <summary>Move the placement cursor by (<paramref name="Dx"/>, <paramref name="Dy"/>) in unit space.</summary>
    public sealed record MarkupMoveCursor(double Dx, double Dy) : ChatAppMsg;

    /// <summary>
    ///     Place the active tool at the cursor (keyboard placement). Arrow and
    ///     rectangle land with a default size; text needs
    ///     <see cref="MarkupOverlayState.PendingText" /> and is a no-op while
    ///     it is empty. Clears the error and, for text, the pending buffer.
    /// </summary>
    public sealed record MarkupPlace : ChatAppMsg;

    /// <summary>Replace the pending text buffer (host accumulates keystrokes; the reducer clamps length).</summary>
    public sealed record MarkupSetPendingText(string Text) : ChatAppMsg;

    /// <summary>Move the selected annotation (undoable).</summary>
    public sealed record MarkupNudge(double Dx, double Dy) : ChatAppMsg;

    /// <summary>Resize the selected annotation (arrow head / rectangle corner; text is a no-op).</summary>
    public sealed record MarkupResize(double Dx, double Dy) : ChatAppMsg;

    /// <summary>Cycle the selection through the annotations in order.</summary>
    public sealed record MarkupSelectNext : ChatAppMsg;

    /// <summary>Click: select the topmost annotation under the point, or move the cursor there.</summary>
    public sealed record MarkupSelectAt(double X, double Y) : ChatAppMsg;

    /// <summary>Mouse press: hit → select + drag checkpoint; miss → start a placement draft.</summary>
    public sealed record MarkupPressAt(double X, double Y) : ChatAppMsg;

    /// <summary>Mouse drag: move the pressed annotation (transient) or stretch the draft.</summary>
    public sealed record MarkupDragTo(double X, double Y) : ChatAppMsg;

    /// <summary>Mouse release: commit the draft as a new primitive, or end the move.</summary>
    public sealed record MarkupReleaseAt(double X, double Y) : ChatAppMsg;

    /// <summary>Delete the selected annotation.</summary>
    public sealed record MarkupDeleteSelected : ChatAppMsg;

    /// <summary>Undo the last content operation.</summary>
    public sealed record MarkupUndo : ChatAppMsg;

    /// <summary>Redo the last undone operation.</summary>
    public sealed record MarkupRedo : ChatAppMsg;

    /// <summary>Record a successful bake: the annotated copy at <paramref name="Path" />.</summary>
    public sealed record MarkupSaved(string Path) : ChatAppMsg;

    /// <summary>Record a bake/save failure as inline display text (never an exception).</summary>
    public sealed record MarkupFailed(string Error) : ChatAppMsg;
}
