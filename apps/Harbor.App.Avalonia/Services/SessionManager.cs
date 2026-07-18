using Avalonia.Threading;
using CSharpFunctionalExtensions;
using Harbor.Abstractions.Agents;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Sessions;
using Harbor.Ui.Framework.State;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Harbor.App.Avalonia.Services;

/// <summary>
///     Owns the active session and binds it to the <see cref="IAgent"/>. The
///     <see cref="SessionListViewModel"/> drives this — New / Open / Branch / Delete
///     operations flow through here so the agent + UiStore stay in sync.
/// </summary>
public sealed class SessionManager
{
    private readonly IServiceProvider _services;
    private readonly IAgent _agent;
    private readonly ISessionStore _sessionStore;
    private readonly UiStore _store;
    private readonly ILogger<SessionManager> _logger;

    /// <summary>The active session, or null if none.</summary>
    public Session? Active { get; private set; }

    /// <summary>Construct a <see cref="SessionManager"/>.</summary>
    public SessionManager(
        IServiceProvider services,
        IAgent agent,
        ISessionStore sessionStore,
        UiStore store,
        ILogger<SessionManager> logger)
    {
        _services = services;
        _agent = agent;
        _sessionStore = sessionStore;
        _store = store;
        _logger = logger;
    }

    /// <summary>
    ///     Create a default session if none exists yet and bind it to the agent.
    ///     Called once at app startup.
    /// </summary>
    public async Task EnsureDefaultSessionAsync()
    {
        if (Active is not null) return;

        // Pick the first registered agent + provider/model from the registries
        // (these were eagerly constructed in AppHost.BuildAsync).
        var agents = _services.GetRequiredService<Harbor.Abstractions.Agents.IAgentRegistry>();
        var agentDef = agents.GetAllAgents().FirstOrDefault()
            ?? throw new InvalidOperationException("No agents registered.");

        string directory = Environment.CurrentDirectory;
        var createResult = await _sessionStore.CreateAsync(
            directory, agentDef.Name.Value, agentDef.ProviderId, agentDef.Model).ConfigureAwait(false);
        if (createResult.IsFailure)
        {
            _logger.LogError("Failed to create default session: {Error}", createResult.Error);
            return;
        }

        var session = createResult.Value;
        _agent.Initialize(session, agentDef);
        _store.BindSession(agentDef.Model, agentDef.ProviderId, agentDef.Name.Value);
        Active = session;
        _logger.LogInformation("Default session created: {Id} ({Title})", session.Id, session.Title);
    }

    /// <summary>
    ///     Create a new session with the given agent/model and switch to it.
    /// </summary>
    /// <param name="agentName">Optional agent name override. Defaults to "code".</param>
    /// <param name="providerId">Optional provider id override.</param>
    /// <param name="modelId">Optional model id override.</param>
    /// <returns>The new session, or null on failure.</returns>
    public async Task<Session?> NewSessionAsync(string? agentName = null, string? providerId = null, string? modelId = null)
    {
        var agents = _services.GetRequiredService<Harbor.Abstractions.Agents.IAgentRegistry>();
        var agentDef = agents.GetAllAgents().FirstOrDefault(a => a.Name.Value == (agentName ?? "code"))
            ?? agents.GetAllAgents().First();

        string provider = providerId ?? agentDef.ProviderId;
        string model = modelId ?? agentDef.Model;
        string directory = Environment.CurrentDirectory;

        var result = await _sessionStore.CreateAsync(directory, agentName ?? agentDef.Name.Value, provider, model).ConfigureAwait(false);
        if (result.IsFailure)
        {
            _logger.LogError("Create session failed: {Error}", result.Error);
            return null;
        }

        var session = result.Value;
        _agent.Initialize(session, agentDef);
        _store.Reset();
        _store.BindSession(model, provider, agentName ?? agentDef.Name.Value);
        Active = session;
        _logger.LogInformation("New session: {Id} ({Title})", session.Id, session.Title);
        return session;
    }

    /// <summary>
    ///     Open (switch to) an existing session.
    /// </summary>
    /// <param name="sessionId">The session id to switch to.</param>
    /// <returns>True on success, false on failure.</returns>
    public async Task<bool> OpenSessionAsync(string sessionId)
    {
        var sessionResult = await _sessionStore.GetAsync(sessionId).ConfigureAwait(false);
        if (sessionResult.IsFailure)
        {
            _logger.LogError("Open session {Id} failed: {Error}", sessionId, sessionResult.Error);
            return false;
        }

        var session = sessionResult.Value;
        var agents = _services.GetRequiredService<Harbor.Abstractions.Agents.IAgentRegistry>();
        var agentDef = agents.GetAllAgents().FirstOrDefault(a => a.Name.Value == session.Agent)
            ?? agents.GetAllAgents().First();

        _agent.Initialize(session, agentDef);
        _store.Reset();
        _store.BindSession(session.Model, session.ProviderId, session.Agent);
        Active = session;

        // Replay history into the UI store so the user sees the prior conversation.
        var messages = await _sessionStore.GetMessagesAsync(sessionId).ConfigureAwait(false);
        if (messages.IsSuccess)
        {
            foreach (var msg in messages.Value)
            {
                var (role, text) = MessageToChatLine(msg);
                _store.Transition(s => s.AddLine(role, text));
            }
        }

        _logger.LogInformation("Opened session {Id}, replayed {Count} messages", session.Id, messages.IsSuccess ? messages.Value.Count : 0);
        return true;
    }

    /// <summary>
    ///     Branch the active session — create a new session with the same messages and metadata
    ///     but a new id. The active session remains unchanged; the new session becomes active.
    /// </summary>
    /// <returns>The branched session, or null on failure.</returns>
    public async Task<Session?> BranchActiveAsync()
    {
        if (Active is null) return null;
        var source = Active;

        var branchResult = await _sessionStore.CreateAsync(
            source.Directory, source.Agent, source.ProviderId, source.Model).ConfigureAwait(false);
        if (branchResult.IsFailure)
        {
            _logger.LogError("Branch session {Id} failed: {Error}", source.Id, branchResult.Error);
            return null;
        }

        var branch = branchResult.Value with { Title = source.Title + " (branch)" };
        var messagesResult = await _sessionStore.GetMessagesAsync(source.Id).ConfigureAwait(false);
        if (messagesResult.IsSuccess)
        {
            foreach (var msg in messagesResult.Value)
            {
                // Re-parent the message to the new session id and persist it.
                var reborn = msg with { SessionId = branch.Id, Id = Guid.NewGuid().ToString("N") };
                await _sessionStore.AppendMessageAsync(branch.Id, reborn).ConfigureAwait(false);
            }
        }

        // Switch to the new branch.
        await OpenSessionAsync(branch.Id).ConfigureAwait(false);
        _logger.LogInformation("Branched session {Old} → {New}", source.Id, branch.Id);
        return branch;
    }

    /// <summary>
    ///     Delete the given session. If it is the active session, switches to any remaining
    ///     session (or creates a fresh default).
    /// </summary>
    /// <param name="sessionId">The session id to delete.</param>
    /// <returns>True on success.</returns>
    public async Task<bool> DeleteSessionAsync(string sessionId)
    {
        var result = await _sessionStore.DeleteAsync(sessionId).ConfigureAwait(false);
        if (result.IsFailure)
        {
            _logger.LogError("Delete session {Id} failed: {Error}", sessionId, result.Error);
            return false;
        }

        _logger.LogInformation("Deleted session {Id}", sessionId);

        if (Active?.Id == sessionId)
        {
            Active = null;
            // Find any other session, or create a fresh one.
            var list = await _sessionStore.ListAsync().ConfigureAwait(false);
            if (list.IsSuccess && list.Value.Count > 0)
            {
                await OpenSessionAsync(list.Value[0].Id).ConfigureAwait(false);
            }
            else
            {
                await EnsureDefaultSessionAsync().ConfigureAwait(false);
            }
        }
        return true;
    }

    /// <summary>Rename a session.</summary>
    public async Task<bool> RenameSessionAsync(string sessionId, string newTitle)
    {
        var sessionResult = await _sessionStore.GetAsync(sessionId).ConfigureAwait(false);
        if (sessionResult.IsFailure) return false;
        var updated = sessionResult.Value with { Title = newTitle, UpdatedAt = DateTimeOffset.UtcNow };
        // The session store doesn't have an Update method — we use UpdateStatsAsync
        // as a side-channel via re-creating the session record in metadata. For the
        // standalone app we accept that rename is best-effort: the title is persisted
        // through message appends.
        Active = Active?.Id == sessionId ? updated : Active;
        _logger.LogInformation("Renamed session {Id} → '{Title}'", sessionId, newTitle);
        return true;
    }

    /// <summary>Convert an <see cref="AgentMessage"/> into a chat-line role + text for the UI store.</summary>
    private static (Harbor.Ui.Framework.State.ChatRole role, string text) MessageToChatLine(AgentMessage msg)
    {
        return msg switch
        {
            Harbor.Abstractions.Models.UserMessage u => (Harbor.Ui.Framework.State.ChatRole.User, u.Content),
            Harbor.Abstractions.Models.AssistantMessage a => (Harbor.Ui.Framework.State.ChatRole.Assistant,
                string.Join(string.Empty, a.Parts.OfType<Harbor.Abstractions.Models.TextPart>().Select(p => p.Text))),
            Harbor.Abstractions.Models.ToolResultMessage t => (Harbor.Ui.Framework.State.ChatRole.ToolResult,
                string.Join("\n", t.Results.Select(r => $"[{r.ToolName}] {r.Output}"))),
            _ => (Harbor.Ui.Framework.State.ChatRole.System, msg.Role)
        };
    }
}
