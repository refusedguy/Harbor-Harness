using Harbor.Abstractions.Agents;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Sessions;
using Harbor.Ui.Framework.State;
using Microsoft.Extensions.DependencyInjection;
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
    private readonly IServiceProvider _services;
    private readonly ISessionStore _sessionStore;

    /// <summary>Construct a <see cref="SessionSwitcher" />.</summary>
    public SessionSwitcher(
        IAgent agent,
        ISessionStore sessionStore,
        IServiceProvider services,
        ILogger<SessionSwitcher> logger)
    {
        _agent = agent;
        _sessionStore = sessionStore;
        _services = services;
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

        var agents = _services.GetRequiredService<IAgentRegistry>();
        var agentDef = agents.GetAllAgents().FirstOrDefault(a => a.Name.Value == session.Agent)
                       ?? agents.GetAllAgents().First();

        _agent.Initialize(session, agentDef);
        targetStore.Reset();
        targetStore.BindSession(session.Model, session.ProviderId, session.Agent);

        // Replay history from the session store into the per-session UiStore.
        var messages = await _sessionStore.GetMessagesAsync(session.Id).ConfigureAwait(false);
        if (messages.IsSuccess)
        {
            foreach (var msg in messages.Value)
            {
                (var role, string text) = SessionFactory.MessageToChatLine(msg);
                targetStore.Transition(s => s.AddLine(role, text));
            }
        }

        _logger.LogInformation("Opened session {Id}, dir={Dir}, replayed {Count} messages",
            session.Id, session.Directory, messages.IsSuccess ? messages.Value.Count : 0);
        return true;
    }
}
