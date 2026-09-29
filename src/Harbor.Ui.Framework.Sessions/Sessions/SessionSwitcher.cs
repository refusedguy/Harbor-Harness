using System.Collections.Immutable;
using Harbor.Abstractions.Agents;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Sessions;
using Harbor.Ui.Framework.State;
using Microsoft.Extensions.Logging;
namespace Harbor.Ui.Framework.Sessions;
/// <summary>
///     Handles session-switching logic: bind the agent + replay persisted
///     message history into the per-session <see cref="UiStore" />.
/// </summary>
/// <remarks>
///     <para>
///         <b>Per-session UiStore:</b> the switcher no longer owns a
///         <c>_savedLines</c> cache — the per-session UiStore itself IS the
///         saved-state cache. Switching to a previously-visited session just
///         rebinds the ChatViewModel to that session's store; the store's
///         in-memory state already reflects everything that happened while
///         the user was away (including events from a still-running agent).
///     </para>
///     <para>
///         Registered as a singleton in <c>AppHost</c> so tests can verify
///         "switch to A → switch to B → switch back to A restores A's lines"
///         without standing up the full SessionManager graph.
///     </para>
/// </remarks>
public sealed class SessionSwitcher
{
    private readonly IAgent _agent;
    private readonly ILogger<SessionSwitcher> _logger;
    private readonly SessionFactory _sessions;
    private readonly ISessionStore _sessionStore;

    /// <summary>Construct a <see cref="SessionSwitcher" />.</summary>
    /// <param name="agent">The agent instance to bind on each open.</param>
    /// <param name="sessionStore">Persistence each opened session replays from.</param>
    /// <param name="sessions">
    ///     Owns agent resolution (#596). Declared rather than a registry of our own so
    ///     that "which agent does a session that names an unknown one open on?" is answered
    ///     once, beside the default-agent policy it falls back to, instead of being
    ///     re-derived from <c>IAgentRegistry</c> at every call site.
    /// </param>
    /// <param name="logger">Diagnostics sink for the open path.</param>
    public SessionSwitcher(
        IAgent agent,
        ISessionStore sessionStore,
        SessionFactory sessions,
        ILogger<SessionSwitcher> logger)
    {
        _agent = agent;
        _sessionStore = sessionStore;
        _sessions = sessions;
        _logger = logger;
    }

    /// <summary>
    ///     Switch to the target session: bind the agent to it, then replay
    ///     the persisted message history from the session store into the
    ///     provided <paramref name="targetStore" /> (the per-session UiStore).
    /// </summary>
    /// <param name="session">The session to switch to.</param>
    /// <param name="targetStore">
    ///     The per-session UiStore to hydrate with
    ///     history. The caller (<see cref="SessionManager" />) owns this store;
    ///     the switcher just populates it.
    /// </param>
    /// <returns>True on success, false on failure.</returns>
    public async Task<bool> OpenAsync(Session session, UiStore targetStore)
    {
        ArgumentNullException.ThrowIfNull(targetStore);

        // #596: this used to be
        //     GetAllAgents().FirstOrDefault(a => a.Name.Value == session.Agent)
        //     ?? GetAllAgents().First()
        // — the pre-#683 policy, which resolved an unknown agent name to whichever
        // entry the registry enumerated first. AgentRegistry is a
        // ConcurrentDictionary, so "first" is the bucket layout on that run rather
        // than the registration order, and the sibling path in
        // SessionLifecycleService.OpenSessionAsync (which #683 moved onto the named
        // default) disagreed with it. Same session, two answers. Both now ask the
        // factory, and the factory falls back to the same default the startup
        // session is built around.
        var agentDef = _sessions.ResolveAgentForSession(session.Agent);

        _agent.Initialize(session, agentDef);

        // #89: hydrate-then-swap — build the replayed lines off to the side
        // and swap them in with a SINGLE AppMsg (one reducer transition, one
        // store CAS) instead of Reset + BindSession + N×AppendLine, whose N+2
        // separate transitions let a background agent event interleave
        // mid-replay and corrupt the transcript order.
        var messages = await _sessionStore.GetMessagesAsync(session.Id).ConfigureAwait(false);
        var lines = ImmutableArray.CreateBuilder<ChatLine>();
        if (messages.IsSuccess)
        {
            foreach (var msg in messages.Value)
            {
                (var role, string text) = SessionFactory.MessageToChatLine(msg);
                lines.Add(new ChatLine(role, text));
            }
        }

        // The status travels with the hydration: it is the core's own answer
        // for this session, and re-deriving one from the replayed lines is the
        // #687 defect on the switch path.
        targetStore.Dispatch(new ChatAppMsg.HydrateSession(
            session.Model, session.ProviderId, session.Agent, lines.ToImmutable(), session.Status));

        _logger.LogInformation("Opened session {Id}, dir={Dir}, replayed {Count} messages",
            session.Id, session.Directory, messages.IsSuccess ? messages.Value.Count : 0);
        return true;
    }
}
