using Avalonia.Threading;
using CSharpFunctionalExtensions;
using Harbor.Abstractions.Agents;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Sessions;
using Harbor.App.Avalonia.ViewModels;
using Harbor.Desktop.Abstractions.Configuration;
using Harbor.Ui.Framework.State;
using System.Collections.Immutable;
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

    /// <summary>Per-session status tracking.</summary>
    private readonly Dictionary<string, SessionStatus> _sessionStatuses = new();

    /// <summary>Per-session saved chat state (for switching without losing context).</summary>
    private readonly Dictionary<string, ImmutableArray<ChatLine>> _savedLines = new();

    /// <summary>Per-session git info.</summary>
    private readonly Dictionary<string, (string? Branch, bool IsDirty)> _gitInfo = new();

    /// <summary>
    ///     Raised whenever <see cref="SetStatus"/> changes a session's
    ///     status. Subscribers (e.g. <see cref="SessionListViewModel"/>)
    ///     use this to push live updates to the sidebar rows so the status
    ///     dot colour tracks the agent state in real time (idle → working →
    ///     done) instead of being frozen at the last <c>RefreshAsync</c>.
    /// </summary>
    public event Action<string, SessionStatus>? StatusChanged;

    /// <summary>
    ///     Raised whenever <see cref="NotifyMessageCount"/> pushes a fresh
    ///     message count for a session. Subscribers (e.g.
    ///     <see cref="SessionListViewModel"/>) update the corresponding
    ///     sidebar row's <c>MessageCount</c> in place so the count tracks
    ///     new messages without a full <c>RefreshAsync</c> round-trip
    ///     (Task S2 / Problem 2: “stale message count after send”).
    /// </summary>
    public event Action<string, int>? MessageCountChanged;

    /// <summary>The active session, or null if none.</summary>
    public Session? Active { get; private set; }

    /// <summary>Get the status of a session.</summary>
    public SessionStatus GetStatus(string sessionId) =>
        _sessionStatuses.TryGetValue(sessionId, out var s) ? s : SessionStatus.Idle;

    /// <summary>
    ///     Set the status of a session and notify subscribers via
    ///     <see cref="StatusChanged"/> so the sidebar can update the row's
    ///     status dot without a full <c>RefreshAsync</c> round-trip.
    /// </summary>
    public void SetStatus(string sessionId, SessionStatus status)
    {
        if (_sessionStatuses.TryGetValue(sessionId, out var current) && current == status) return;
        _sessionStatuses[sessionId] = status;
        StatusChanged?.Invoke(sessionId, status);
    }

    /// <summary>
    ///     Push a fresh message count for a session. Subscribers (e.g.
    ///     <see cref="SessionListViewModel"/>) update the sidebar row's
    ///     <c>MessageCount</c> in place so the count tracks new messages
    ///     without a full <c>RefreshAsync</c> round-trip (Task S2 /
    ///     Problem 2).
    /// </summary>
    /// <param name="sessionId">The session id.</param>
    /// <param name="count">The new message count.</param>
    public void NotifyMessageCount(string sessionId, int count)
    {
        MessageCountChanged?.Invoke(sessionId, count);
    }

    /// <summary>Get git info for a session's working directory.</summary>
    public (string? Branch, bool IsDirty) GetGitInfo(string sessionId)
    {
        if (_gitInfo.TryGetValue(sessionId, out var info))
            return info;
        return (null, false);
    }

    /// <summary>Refresh git info for a session.</summary>
    public void RefreshGitInfo(string sessionId, string directory)
    {
        var gitService = _services.GetService<GitService>();
        if (gitService is null) return;
        var info = gitService.GetGitStatus(directory);
        _gitInfo[sessionId] = info;
    }

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
    ///     Called once at app startup. Reads the fresh <see cref="CommonConfig"/>
    ///     from disk so the wizard's saved provider/model take effect even though
    ///     the DI singleton <see cref="CommonConfig"/> was loaded before the wizard
    ///     ran.
    /// </summary>
    public async Task EnsureDefaultSessionAsync()
    {
        if (Active is not null) return;

        // Pick the first registered agent + provider/model from the registries
        // (these were eagerly constructed in AppHost.BuildAsync).
        var agents = _services.GetRequiredService<Harbor.Abstractions.Agents.IAgentRegistry>();
        var agentDef = agents.GetAllAgents().FirstOrDefault()
            ?? throw new InvalidOperationException("No agents registered.");

        // Override the agent definition with the fresh CommonConfig values.
        // The agent registry was built from the startup CommonConfig snapshot
        // (which is stale if the wizard just ran). Reloading here picks up the
        // user's wizard selections (DefaultProvider / DefaultModel).
        var (providerId, modelId) = await ResolveProviderModelFromConfigAsync().ConfigureAwait(false);
        if (!string.IsNullOrEmpty(providerId) && !string.IsNullOrEmpty(modelId))
        {
            agentDef = agentDef.WithModel(modelId, providerId);
        }

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
        // Reset rendering for the default session.
        var chatVm3 = _services.GetService<ChatViewModel>();
        Dispatcher.UIThread.Post(() => chatVm3?.ResetRendering());
        Active = session;
        RefreshGitInfo(session.Id, session.Directory);
        SetStatus(session.Id, SessionStatus.Idle);
        _logger.LogInformation("Default session created: {Id} ({Title}) dir={Dir} provider={Provider} model={Model}",
            session.Id, session.Title, session.Directory, agentDef.ProviderId, agentDef.Model);
    }

    /// <summary>
    ///     Rebind the active session to the freshly-loaded <see cref="CommonConfig"/>
    ///     values. Called by <c>App.axaml.cs</c> after the onboarding wizard saves
    ///     a new config — the wizard runs AFTER <see cref="AppHost.BuildAsync"/>,
    ///     so the agent registry + active session were initialized with the
    ///     pre-wizard defaults. This method picks up the user's wizard selections
    ///     (DefaultProvider / DefaultModel) without requiring an app restart.
    /// </summary>
    /// <remarks>
    ///     If no active session exists, delegates to <see cref="EnsureDefaultSessionAsync"/>.
    ///     Otherwise, derives a new <see cref="AgentDefinition"/> from the "code"
    ///     agent with the fresh provider/model, re-initializes the agent, and
    ///     updates the in-memory session record + UiStore binding so the status
    ///     bar reflects the change. The session store is NOT updated (it has no
    ///     metadata-update API) — the stale persisted record is harmless and will
    ///     be replaced the next time the user creates a session.
    /// </remarks>
    public async Task RebindFromCommonConfigAsync()
    {
        if (Active is null)
        {
            await EnsureDefaultSessionAsync().ConfigureAwait(false);
            return;
        }

        var agents = _services.GetRequiredService<Harbor.Abstractions.Agents.IAgentRegistry>();
        var agentDef = agents.GetAllAgents().FirstOrDefault(a => a.Name.Value == "code")
            ?? agents.GetAllAgents().FirstOrDefault()
            ?? throw new InvalidOperationException("No agents registered.");

        var (providerId, modelId) = await ResolveProviderModelFromConfigAsync().ConfigureAwait(false);
        if (string.IsNullOrEmpty(providerId) || string.IsNullOrEmpty(modelId))
        {
            _logger.LogInformation("RebindFromCommonConfig: no provider/model in config, keeping current agent");
            return;
        }

        agentDef = agentDef.WithModel(modelId, providerId);
        _agent.Initialize(Active, agentDef);
        _store.BindSession(agentDef.Model, agentDef.ProviderId, agentDef.Name.Value);
        // Update the in-memory session record so the status bar shows the new
        // provider/model. The persisted session record is NOT updated (ISessionStore
        // has no metadata-update API) — the stale persisted copy is harmless.
        Active = Active with { ProviderId = providerId, Model = modelId };
        _logger.LogInformation("Rebound session {Id} to provider={Provider} model={Model}",
            Active.Id, providerId, modelId);
    }

    /// <summary>
    ///     Load the fresh <see cref="CommonConfig"/> from disk and split the
    ///     DefaultProvider/DefaultModel into a (providerId, modelId) pair.
    ///     Returns (null, null) if the config can't be loaded.
    /// </summary>
    private async Task<(string? ProviderId, string? ModelId)> ResolveProviderModelFromConfigAsync()
    {
        var configStore = _services.GetService<ICommonConfigStore>();
        if (configStore is null) return (null, null);

        var result = await configStore.LoadAsync().ConfigureAwait(false);
        if (!result.IsSuccess) return (null, null);

        var cfg = result.Value;
        string provider = cfg.DefaultProvider;
        string model = cfg.DefaultModel;
        // The model may already start with the provider prefix (e.g. the user
        // typed "kilocode/tencent/hy3:free"). Strip it so we get the bare model id.
        string prefix = provider + "/";
        if (model.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            model = model[prefix.Length..];
        }
        return (provider, model);
    }

    /// <summary>
    ///     Create a new session with the given agent/model and switch to it.
    /// </summary>
    /// <param name="agentName">Optional agent name override. Defaults to "code".</param>
    /// <param name="providerId">Optional provider id override.</param>
    /// <param name="modelId">Optional model id override.</param>
    /// <param name="workingDirectory">Optional working directory for the session.</param>
    /// <returns>The new session, or null on failure.</returns>
    public async Task<Session?> NewSessionAsync(string? agentName = null, string? providerId = null, string? modelId = null, string? workingDirectory = null)
    {
        var agents = _services.GetRequiredService<Harbor.Abstractions.Agents.IAgentRegistry>();
        var agentDef = agents.GetAllAgents().FirstOrDefault(a => a.Name.Value == (agentName ?? "code"))
            ?? agents.GetAllAgents().First();

        // Use config provider/model as default (not the agent definition defaults).
        var (configProvider, configModel) = await ResolveProviderModelFromConfigAsync().ConfigureAwait(false);
        string provider = providerId ?? configProvider ?? agentDef.ProviderId;
        string model = modelId ?? configModel ?? agentDef.Model;
        string directory = Environment.CurrentDirectory;

        // Override the agent definition with the resolved provider/model.
        agentDef = agentDef.WithModel(model, provider);

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
        // Reset the ChatViewModel's rendering state so old messages are
        // cleared and the new session's messages render fresh. Without this,
        // ChatViewModel._renderedLineCount stays at its old value and
        // OnStoreChanged skips the new session's lines (they're appended to
        // UiStore via Transition but the renderer thinks they were already
        // shown).
        var newChatVm = _services.GetService<ChatViewModel>();
        if (newChatVm is not null)
        {
            Dispatcher.UIThread.Post(() => newChatVm.ResetRendering());
            _logger.LogInformation("NewSession: scheduled ChatViewModel.ResetRendering for new session {Id}", session.Id);
        }
        Active = session;
        // Clear the token-usage chart so it reflects only the new session's
        // usage (UiStore.Reset zeroes Cost, but the chart's bars + sparkline
        // are session-scoped state and would otherwise leak the previous
        // session's history into the new one — Task S2 / Problem 3).
        ClearTokenUsageForActiveSession();
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

        // Save current chat state before switching (so agent output isn't lost).
        if (Active is not null)
        {
            _savedLines[Active.Id] = _store.State.Lines;
        }

        var agents = _services.GetRequiredService<Harbor.Abstractions.Agents.IAgentRegistry>();
        var agentDef = agents.GetAllAgents().FirstOrDefault(a => a.Name.Value == session.Agent)
            ?? agents.GetAllAgents().First();

        _agent.Initialize(session, agentDef);
        _store.Reset();
        // Reset ChatViewModel rendering state so old messages are cleared
        // and new session's messages are rendered fresh.
        var chatVm = _services.GetService<ChatViewModel>();
        Dispatcher.UIThread.Post(() => chatVm?.ResetRendering());
        _store.BindSession(session.Model, session.ProviderId, session.Agent);

        // Reset the ChatViewModel's rendering state so old messages are
        // cleared and the new session's messages render fresh. Without this,
        // ChatViewModel._renderedLineCount stays at its old value, the
        // previous session's chat rows linger in Lines, and OnStoreChanged
        // silently skips every replayed line below (it thinks they were
        // already rendered). The user-visible symptom was: switching sessions
        // had no effect — the chat still showed the old conversation.
        var openChatVm = _services.GetService<ChatViewModel>();
        if (openChatVm is not null)
        {
            Dispatcher.UIThread.Post(() => openChatVm.ResetRendering());
            _logger.LogInformation("OpenSession: scheduled ChatViewModel.ResetRendering for new session {Id}", session.Id);
        }

        // Restore saved chat state for this session (if any), otherwise replay from store.
        if (_savedLines.TryGetValue(session.Id, out var savedLines) && savedLines.Length > 0)
        {
            _store.Transition(s => s with { Lines = savedLines });
        }
        else
        {
            // Replay history from the session store.
            var messages = await _sessionStore.GetMessagesAsync(sessionId).ConfigureAwait(false);
            if (messages.IsSuccess)
            {
                foreach (var msg in messages.Value)
                {
                    var (role, text) = MessageToChatLine(msg);
                    _store.Transition(s => s.AddLine(role, text));
                }
            }
        }

        // Refresh git info for this session's working directory.
        RefreshGitInfo(session.Id, session.Directory);

        Active = session;
        // Clear the token-usage chart so it reflects only the newly-opened
        // session's usage (the previous session's bars would otherwise
        // linger — Task S2 / Problem 3).
        ClearTokenUsageForActiveSession();
        _logger.LogInformation("Opened session {Id}, dir={Dir}", session.Id, session.Directory);
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

    /// <summary>
    ///     Rename a session. <b>NOT YET SUPPORTED</b> — the underlying
    ///     <see cref="ISessionStore"/> has no <c>UpdateSessionMetadataAsync</c>
    ///     API (only <c>CreateAsync</c>/<c>UpdateStatsAsync</c>), so persisting
    ///     a title change is not possible without a sidecar file. We log a
    ///     warning and return <c>false</c> so the caller can surface an honest
    ///     "coming in v0.8" toast. Tracked in the roadmap as session-metadata
    ///     persistence.
    /// </summary>
    /// <param name="sessionId">The session id to rename.</param>
    /// <param name="newTitle">The new title.</param>
    /// <returns><c>false</c> — rename is not yet persisted.</returns>
    public Task<bool> RenameSessionAsync(string sessionId, string newTitle)
    {
        _logger.LogWarning(
            "Rename session {Id} → '{Title}' ignored — ISessionStore has no metadata-update API. Coming in v0.8.",
            sessionId, newTitle);
        return Task.FromResult(false);
    }

    /// <summary>
    ///     Resolve the singleton <see cref="TokenUsageViewModel"/> from the
    ///     DI container and clear its bars + sparkline + baseline. Called
    ///     on every session switch (open + new) so the chart tracks only
    ///     the active session's tokens, not the cumulative total across
    ///     every session the user has ever opened (Task S2 / Problem 3).
    /// </summary>
    private void ClearTokenUsageForActiveSession()
    {
        _services.GetService<TokenUsageViewModel>()?.Clear();
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
