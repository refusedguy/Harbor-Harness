using System.Collections.Concurrent;
using Harbor.Abstractions.Models;
namespace Harbor.Ui.Framework.Sessions;
/// <summary>
///     Owns the per-session <see cref="SessionContext" /> registry and the
///     active-session pointer. Extracted from the <see cref="SessionManager" />
///     facade (issue #189): context routing (which store an agent event lands
///     in) is now independent of lifecycle orchestration and status tracking.
/// </summary>
/// <remarks>
///     <para>
///         <b>Per-session UiStore (concurrent agents):</b> each open session
///         has its own <see cref="SessionContext" /> / <see cref="UiStore" />
///         held in the live map. When the user switches sessions,
///         the agent in the previous session is <b>not</b> aborted — its
///         events keep flowing into the OLD session's UiStore (routed by
///         <c>AppHost</c>'s EventBus subscriber using
///         <see cref="AgentStartEvent.SessionId" />). The UI rebinds to the
///         NEW session's UiStore via <see cref="ChatViewModel.RebindToStore" />.
///     </para>
///     <para>
///         Thread-safety (issue #81): <see cref="GetContext" /> runs on the
///         EventBus publisher (tool) thread while open/switch/delete run on
///         the UI thread — both maps are concurrent so readers never tear.
///     </para>
/// </remarks>
public sealed class SessionEventRouter
{
    /// <summary>
    ///     Per-session contexts — one <see cref="SessionContext" /> per open
    ///     session, each with its own <see cref="UiStore" />. Keyed by session
    ///     id. Used by <c>AppHost</c>'s EventBus subscriber to route agent
    ///     events to the correct store so a background agent in session A
    ///     doesn't leak messages into session B's chat transcript.
    ///     Concurrent map (issue #81): <see cref="GetContext" /> runs on the
    ///     EventBus publisher (tool) thread while open/switch/delete run on
    ///     the UI thread — a plain <c>Dictionary</c> tears under that pairing.
    /// </summary>
    private readonly ConcurrentDictionary<string, SessionContext> _contexts = new(StringComparer.Ordinal);

    /// <summary>
    ///     Parked (tombstoned) contexts for deleted sessions (#89). A deleted
    ///     session's background agent may still emit events while the switch
    ///     to the next session is awaited; <see cref="GetContext" /> falls
    ///     back to the parked store so those late events land in the dead
    ///     session's own transcript instead of leaking into the newly-active
    ///     session (via the ActiveContext fallback in the event router) or
    ///     dropping silently. Parked contexts are never rebound to the UI —
    ///     they are pure event sinks, kept for the app lifetime. Concurrent
    ///     map for the same cross-thread reason as <see cref="_contexts" />
    ///     (issue #81).
    /// </summary>
    private readonly ConcurrentDictionary<string, SessionContext> _tombstones = new(StringComparer.Ordinal);

    /// <summary>
    ///     The active <see cref="SessionContext" /> (holds the active session
    ///     + its UiStore + status + git info), or null if none. The ChatViewModel
    ///     is bound to <see cref="SessionContext.Store" /> of this context.
    /// </summary>
    public SessionContext? ActiveContext { get; set; }

    /// <summary>The active session, or null if none.</summary>
    public Session? Active => ActiveContext?.Session;

    /// <summary>
    ///     Look up a <see cref="SessionContext" /> by session id. Returns null
    ///     if no context has been created for this session (e.g. the session
    ///     exists in the store but has never been opened in this app run).
    ///     Used by <c>AppHost</c>'s EventBus subscriber to route agent events
    ///     to the correct per-session UiStore.
    ///     Falls back to the parked (tombstoned) context of a deleted session
    ///     (#89) so late background events still have a home and never leak
    ///     into the active session's transcript.
    /// </summary>
    /// <param name="sessionId">The session id to look up.</param>
    /// <returns>The <see cref="SessionContext" />, or null.</returns>
    public SessionContext? GetContext(string sessionId)
    {
        if (_contexts.TryGetValue(sessionId, out var ctx)) return ctx;
        _tombstones.TryGetValue(sessionId, out var parked);
        return parked;
    }

    /// <summary>
    ///     Get-or-create the <see cref="SessionContext" /> for a session.
    ///     Single atomic <c>GetOrAdd</c> (issue #81): two concurrent opens of
    ///     the same session must observe ONE context — check-then-set could
    ///     build two <see cref="UiStore" />s and split routed events between
    ///     them, orphaning one transcript.
    /// </summary>
    public SessionContext GetOrCreateContext(Session session) =>
        _contexts.GetOrAdd(session.Id, static (_, s) => new SessionContext(s), session);

    /// <summary>
    ///     Park the live context of a deleted session so late background
    ///     events still have a home, then remove it from the live map.
    ///     Park-before-remove (issue #81): a concurrent GetContext on the
    ///     EventBus thread must never observe the gap between live-removal
    ///     and tombstoning — it sees the live entry first, then the parked
    ///     one, never neither.
    /// </summary>
    /// <param name="sessionId">The deleted session id.</param>
    /// <returns>The parked context, or null when the session was never open.</returns>
    public SessionContext? ParkContext(string sessionId)
    {
        if (_contexts.TryGetValue(sessionId, out var live))
            _tombstones[sessionId] = live;
        _contexts.TryRemove(sessionId, out _);
        return live;
    }
}
