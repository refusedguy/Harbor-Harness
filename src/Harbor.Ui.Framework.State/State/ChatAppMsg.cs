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
}
