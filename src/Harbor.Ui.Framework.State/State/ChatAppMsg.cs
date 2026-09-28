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
    public sealed record HydrateSession(
        string Model,
        string Provider,
        string AgentName,
        ImmutableArray<ChatLine> Lines) : ChatAppMsg;

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
}
