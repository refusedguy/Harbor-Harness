using Harbor.Abstractions.Agents;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Sessions;
using Harbor.Ui.Framework.Services;
using Harbor.Ui.Framework.Sessions;
using Harbor.Ui.Framework.State;
using Harbor.Ui.Framework.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
namespace Harbor.Ui.Framework.Sessions;
/// <summary>
///     Facade that owns the active session and delegates creation, switching,
///     git-tracking, and status-tracking to dedicated services. The sidebar
///     (<see cref="SessionListViewModel" />) drives this — New / Open / Branch /
///     Delete operations flow through here so the agent + UiStore stay in sync.
/// </summary>
/// <remarks>
///     <para>
///         <b>Per-session UiStore (concurrent agents):</b> each open session
///         has its own <see cref="SessionContext" /> / <see cref="UiStore" />
///         held in <see cref="_contexts" />. When the user switches sessions,
///         the agent in the previous session is <b>not</b> aborted — its
///         events keep flowing into the OLD session's UiStore (routed by
///         <c>AppHost</c>'s EventBus subscriber using
///         <see cref="AgentStartEvent.SessionId" />). The UI rebinds to the
///         NEW session's UiStore via <see cref="ChatViewModel.RebindToStore" />.
///         This is the user-visible fix for
///         <c>
///             "я хочу чтобы агенты не останавливались а я мог их в разных
///             сессиях останавливать работающими"
///         </c>
///         .
///     </para>
///     <para>
///         <b>Decomposition:</b>
///         <list type="bullet">
///             <item><see cref="SessionFactory" /> — creates sessions.</item>
///             <item><see cref="SessionSwitcher" /> — bind agent + replay history into the per-session UiStore.</item>
///             <item><see cref="SessionGitTracker" /> — per-session git status cache.</item>
///             <item><see cref="SessionStatusTracker" /> — per-session status + event sink.</item>
///         </list>
///         Each subordinate service is DI-registered so it can be mocked in
///         tests; this facade is just orchestration.
///     </para>
/// </remarks>
public sealed class SessionManager : ISessionManager
{
    private readonly IAgent _agent;
    private readonly IChatViewBinder _chatViewBinder;

    /// <summary>
    ///     Per-session contexts — one <see cref="SessionContext" /> per open
    ///     session, each with its own <see cref="UiStore" />. Keyed by session
    ///     id. Used by <c>AppHost</c>'s EventBus subscriber to route agent
    ///     events to the correct store so a background agent in session A
    ///     doesn't leak messages into session B's chat transcript.
    /// </summary>
    private readonly Dictionary<string, SessionContext> _contexts = new();
    private readonly SessionFactory _factory;
    private readonly SessionGitTracker _gitTracker;
    private readonly ILogger<SessionManager> _logger;
    private readonly IServiceProvider _services;
    private readonly ISessionStore _sessionStore;
    private readonly SessionStatusTracker _statusTracker;
    private readonly UiStore _store;
    private readonly SessionSwitcher _switcher;

    /// <summary>Construct a <see cref="SessionManager" /> facade.</summary>
    public SessionManager(
        IServiceProvider services,
        IAgent agent,
        ISessionStore sessionStore,
        UiStore store,
        SessionFactory factory,
        SessionSwitcher switcher,
        SessionGitTracker gitTracker,
        SessionStatusTracker statusTracker,
        IChatViewBinder chatViewBinder,
        ILogger<SessionManager> logger)
    {
        _services = services;
        _agent = agent;
        _sessionStore = sessionStore;
        _store = store;
        _factory = factory;
        _switcher = switcher;
        _gitTracker = gitTracker;
        _statusTracker = statusTracker;
        _chatViewBinder = chatViewBinder;
        _logger = logger;
    }

    /// <summary>The active session, or null if none.</summary>
    public Session? Active => ActiveContext?.Session;

    /// <summary>
    ///     The active <see cref="SessionContext" /> (holds the active session
    ///     + its UiStore + status + git info), or null if none. The ChatViewModel
    ///     is bound to <see cref="SessionContext.Store" /> of this context.
    /// </summary>
    public SessionContext? ActiveContext { get; private set; }

    /// <summary>
    ///     Raised whenever a session's status changes. Forwards from
    ///     <see cref="SessionStatusTracker.StatusChanged" /> so subscribers
    ///     don't need to know about the tracker decomposition.
    /// </summary>
    public event Action<string, SessionStatus>? StatusChanged
    {
        add => _statusTracker.StatusChanged += value;
        remove => _statusTracker.StatusChanged -= value;
    }

    /// <summary>
    ///     Raised whenever a session's message count is pushed. Forwards from
    ///     <see cref="SessionStatusTracker.MessageCountChanged" />.
    /// </summary>
    public event Action<string, int>? MessageCountChanged
    {
        add => _statusTracker.MessageCountChanged += value;
        remove => _statusTracker.MessageCountChanged -= value;
    }

    /// <summary>
    ///     Look up a <see cref="SessionContext" /> by session id. Returns null
    ///     if no context has been created for this session (e.g. the session
    ///     exists in the store but has never been opened in this app run).
    ///     Used by <c>AppHost</c>'s EventBus subscriber to route agent events
    ///     to the correct per-session UiStore.
    /// </summary>
    /// <param name="sessionId">The session id to look up.</param>
    /// <returns>The <see cref="SessionContext" />, or null.</returns>
    public SessionContext? GetContext(string sessionId) =>
        _contexts.TryGetValue(sessionId, out var ctx) ? ctx : null;

    /// <summary>Get the status of a session.</summary>
    public SessionStatus GetStatus(string sessionId) => _statusTracker.Get(sessionId);

    /// <summary>Set the status of a session (forwards to <see cref="SessionStatusTracker" />).</summary>
    public void SetStatus(string sessionId, SessionStatus status) =>
        _statusTracker.Set(sessionId, status);

    /// <summary>Push a fresh message count for a session (forwards to <see cref="SessionStatusTracker" />).</summary>
    /// <param name="sessionId">The session id.</param>
    /// <param name="count">The new message count.</param>
    public void NotifyMessageCount(string sessionId, int count) =>
        _statusTracker.NotifyMessageCount(sessionId, count);

    /// <summary>Get git info for a session's working directory (forwards to <see cref="SessionGitTracker" />).</summary>
    public (string? Branch, bool IsDirty) GetGitInfo(string sessionId) =>
        _gitTracker.Get(sessionId);

    /// <summary>Refresh git info for a session (forwards to <see cref="SessionGitTracker" />).</summary>
    public void RefreshGitInfo(string sessionId, string directory) =>
        _gitTracker.Refresh(sessionId, directory, _services.GetService<GitService>());

    /// <summary>
    ///     Create a default session if none exists yet and bind it to the agent.
    ///     Called once at app startup. Reads the fresh <see cref="CommonConfig" />
    ///     from disk so the wizard's saved provider/model take effect.
    /// </summary>
    public async Task EnsureDefaultSessionAsync()
    {
        if (ActiveContext is not null) return;

        var session = await _factory.CreateDefaultAsync().ConfigureAwait(false);
        if (session is null) return;

        var ctx = GetOrCreateContext(session);
        if (!await _switcher.OpenAsync(session, ctx.Store).ConfigureAwait(false)) return;
        ctx.StoreWasHydrated = true;
        ActiveContext = ctx;
        RefreshGitInfo(session.Id, session.Directory);
        SetStatus(session.Id, SessionStatus.Idle);
        RebindChatViewModel(ctx, ctx.RenderedLineCount);
    }

    /// <summary>
    ///     Rebind the active session to the freshly-loaded
    ///     <see cref="CommonConfig" /> values. Called by <c>App.axaml.cs</c>
    ///     after the onboarding wizard saves a new config.
    /// </summary>
    public async Task RebindFromCommonConfigAsync()
    {
        if (ActiveContext is null)
        {
            await EnsureDefaultSessionAsync().ConfigureAwait(false);
            return;
        }

        await AbortRunningAgentAsync().ConfigureAwait(false);

        var agents = _services.GetRequiredService<IAgentRegistry>();
        var agentDef = agents.GetAllAgents().FirstOrDefault(a => a.Name.Value == "code")
                       ?? agents.GetAllAgents().FirstOrDefault()
                       ?? throw new InvalidOperationException("No agents registered.");

        (string? providerId, string? modelId) = await _factory.ResolveProviderModelFromConfigAsync().ConfigureAwait(false);
        if (string.IsNullOrEmpty(providerId) || string.IsNullOrEmpty(modelId))
        {
            _logger.LogInformation("RebindFromCommonConfig: no provider/model in config, keeping current agent");
            return;
        }

        agentDef = agentDef.WithModel(modelId, providerId);
        var session = ActiveContext.Session with { ProviderId = providerId, Model = modelId };
        ActiveContext.Session = session;
        _agent.Initialize(session, agentDef);
        ActiveContext.Store.BindSession(agentDef.Model, agentDef.ProviderId, agentDef.Name.Value);
        _logger.LogInformation("Rebound session {Id} to provider={Provider} model={Model}",
            session.Id, providerId, modelId);
    }

    /// <summary>
    ///     Create a new session with the given agent/model and switch to it.
    ///     The previously-active session's agent is <b>not</b> aborted —
    ///     it continues running in the background and its events keep
    ///     flowing into its own UiStore.
    /// </summary>
    public async Task<Session?> NewSessionAsync(string? agentName = null, string? providerId = null, string? modelId = null, string? workingDirectory = null)
    {
        var session = await _factory.CreateNewAsync(agentName, providerId, modelId, workingDirectory).ConfigureAwait(false);
        if (session is null) return null;

        var ctx = GetOrCreateContext(session);
        if (!await _switcher.OpenAsync(session, ctx.Store).ConfigureAwait(false)) return null;
        ctx.StoreWasHydrated = true;
        SaveActiveRenderedLineCount();
        ActiveContext = ctx;
        ClearTokenUsageForActiveSession();
        RebindChatViewModel(ctx, ctx.RenderedLineCount);
        return session;
    }

    /// <summary>
    ///     Open (switch to) an existing session. The currently-active
    ///     session's agent is <b>not</b> aborted — it keeps running in the
    ///     background and its events keep flowing into its own UiStore.
    ///     The ChatViewModel rebinds to the target session's UiStore.
    /// </summary>
    public async Task<bool> OpenSessionAsync(string sessionId)
    {
        var sessionResult = await _sessionStore.GetAsync(sessionId).ConfigureAwait(false);
        if (sessionResult.IsFailure)
        {
            _logger.LogError("Open session {Id} failed: {Error}", sessionId, sessionResult.Error);
            return false;
        }

        var session = sessionResult.Value;
        var ctx = GetOrCreateContext(session);

        if (!ctx.StoreWasHydrated)
        {
            if (!await _switcher.OpenAsync(session, ctx.Store).ConfigureAwait(false)) return false;
            ctx.StoreWasHydrated = true;
        }

        RefreshGitInfo(session.Id, session.Directory);
        SaveActiveRenderedLineCount();
        ActiveContext = ctx;
        ClearTokenUsageForActiveSession();
        RebindChatViewModel(ctx, ctx.RenderedLineCount);
        return true;
    }

    /// <summary>
    ///     Branch the active session — create a new session with the same
    ///     messages and metadata but a new id, then switch to the branch.
    /// </summary>
    public async Task<Session?> BranchActiveAsync()
    {
        if (ActiveContext is null) return null;
        var branch = await _factory.CreateBranchAsync(ActiveContext.Session).ConfigureAwait(false);
        if (branch is null) return null;
        await OpenSessionAsync(branch.Id).ConfigureAwait(false);
        return branch;
    }

    /// <summary>
    ///     Delete the given session. If it is the active session, switches to
    ///     any remaining session (or creates a fresh default). Also removes
    ///     the per-session <see cref="SessionContext" /> from <see cref="_contexts" />.
    /// </summary>
    public async Task<bool> DeleteSessionAsync(string sessionId)
    {
        var result = await _sessionStore.DeleteAsync(sessionId).ConfigureAwait(false);
        if (result.IsFailure)
        {
            _logger.LogError("Delete session {Id} failed: {Error}", sessionId, result.Error);
            return false;
        }

        _contexts.Remove(sessionId);
        _logger.LogInformation("Deleted session {Id}", sessionId);

        if (ActiveContext?.Session.Id == sessionId)
        {
            ActiveContext = null;
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
    ///     <see cref="ISessionStore" /> has no metadata-update API. Logs a
    ///     warning and returns <c>false</c>.
    /// </summary>
    public Task<bool> RenameSessionAsync(string sessionId, string newTitle)
    {
        _logger.LogWarning(
            "Rename session {Id} → '{Title}' ignored — ISessionStore has no metadata-update API. Coming in v0.8.",
            sessionId, newTitle);
        return Task.FromResult(false);
    }

    /// <summary>
    ///     Resolve the singleton <see cref="TokenUsageViewModel" /> from the
    ///     DI container and clear its bars + sparkline + baseline. Called
    ///     on every session switch (open + new) so the chart tracks only
    ///     the active session's tokens.
    /// </summary>
    private void ClearTokenUsageForActiveSession() => _services.GetService<TokenUsageViewModel>()?.Clear();

    /// <summary>
    ///     Get-or-create the <see cref="SessionContext" /> for a session.
    /// </summary>
    private SessionContext GetOrCreateContext(Session session)
    {
        if (_contexts.TryGetValue(session.Id, out var existing)) return existing;
        var ctx = new SessionContext(session);
        _contexts[session.Id] = ctx;
        return ctx;
    }

    /// <summary>
    ///     Persist the ChatViewModel's <c>_renderedLineCount</c> into the
    ///     currently-active <see cref="SessionContext.RenderedLineCount" />.
    /// </summary>
    private void SaveActiveRenderedLineCount()
    {
        if (ActiveContext is null) return;
        ActiveContext.RenderedLineCount = _chatViewBinder.GetRenderedLineCount();
    }

    /// <summary>
    ///     Rebind the singleton chat view-model to a different session's
    ///     <see cref="UiStore" />. Delegates to <see cref="IChatViewBinder" />.
    /// </summary>
    private void RebindChatViewModel(SessionContext ctx, int savedRenderedLineCount)
    {
        _chatViewBinder.Rebind(ctx.Store, savedRenderedLineCount);
        _logger.LogInformation(
            "RebindChatViewModel → session {Id} (renderedLineCount={Count})",
            ctx.Session.Id, savedRenderedLineCount);
    }

    /// <summary>
    ///     Abort any in-flight <see cref="IAgent.PromptAsync" /> call and wait
    ///     (bounded) for the agent to return to idle.
    /// </summary>
    private async Task AbortRunningAgentAsync()
    {
        if (_agent.State?.IsRunning != true)
        {
            _agent.ResetAbortSource();
            return;
        }

        _logger.LogInformation("Aborting in-flight agent before rebind (session={OldSession})",
            _agent.State.SessionId);

        _agent.AbortSource.Cancel();

        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await _agent.WaitForIdleAsync(timeout.Token).ConfigureAwait(false);
            _logger.LogInformation("Agent went idle after abort");
        }
        catch (OperationCanceledException ex)
        {
            _logger.LogWarning(ex, "Agent did not go idle within 3s after abort — force-continuing");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error waiting for agent idle after abort");
        }

        _agent.ResetAbortSource();
    }
}
