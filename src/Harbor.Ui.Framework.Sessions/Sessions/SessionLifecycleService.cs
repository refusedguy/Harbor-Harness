using System.Collections.Immutable;
using CSharpFunctionalExtensions;
using Harbor.Abstractions.Agents;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Sessions;
using Harbor.Ui.Framework.State;
using Microsoft.Extensions.Logging;
namespace Harbor.Ui.Framework.Sessions;
/// <summary>
///     Session lifecycle orchestration: create/open/branch/delete/rename plus
///     git refresh and config rebind. Extracted from the
///     <see cref="SessionManager" /> facade (issue #189) — the facade keeps
///     only delegation, all orchestration lives here.
/// </summary>
/// <remarks>
///     <para>
///         Decomposition collaborators:
///         <list type="bullet">
///             <item><see cref="SessionEventRouter" /> — per-session contexts + active pointer.</item>
///             <item><see cref="SessionFactory" /> — creates sessions.</item>
///             <item><see cref="SessionSwitcher" /> — bind agent + replay history into the per-session UiStore.</item>
///             <item><see cref="SessionGitTracker" /> — per-session git status cache.</item>
///             <item><see cref="SessionStatusService" /> — per-session status + event sink.</item>
///         </list>
///         Optional host-only collaborators arrive as explicit Func-factories
///         (<see cref="SessionOptionalFactories" />), never via Service Locator.
///     </para>
/// </remarks>
public sealed class SessionLifecycleService : ISessionLifecycle
{
    private readonly IAgent _agent;
    private readonly IAgentRegistry _agents;
    private readonly IChatViewBinder _chatViewBinder;
    private readonly SessionFactory _factory;
    private readonly SessionOptionalFactories _factories;
    private readonly SessionGitTracker _gitTracker;
    private readonly ILogger<SessionLifecycleService> _logger;
    private readonly SessionEventRouter _router;
    private readonly ISessionStore _sessionStore;
    private readonly SessionStatusService _status;
    private readonly SessionSwitcher _switcher;

    /// <summary>Construct a <see cref="SessionLifecycleService" />.</summary>
    public SessionLifecycleService(
        SessionEventRouter router,
        SessionFactory factory,
        SessionSwitcher switcher,
        ISessionStore sessionStore,
        IAgent agent,
        IAgentRegistry agents,
        SessionStatusService status,
        SessionGitTracker gitTracker,
        IChatViewBinder chatViewBinder,
        SessionOptionalFactories factories,
        ILogger<SessionLifecycleService> logger)
    {
        _router = router;
        _factory = factory;
        _switcher = switcher;
        _sessionStore = sessionStore;
        _agent = agent;
        _agents = agents;
        _status = status;
        _gitTracker = gitTracker;
        _chatViewBinder = chatViewBinder;
        _factories = factories;
        _logger = logger;
    }

    /// <summary>Refresh git info for a session (forwards to <see cref="SessionGitTracker" />).</summary>
    public void RefreshGitInfo(string sessionId, string directory) =>
        // #63 legitimate: optional dependency — GitService is host-only
        // (desktop); headless/test hosts refresh without git enrichment.
        _gitTracker.Refresh(sessionId, directory, _factories.GitService());

    /// <summary>
    ///     Create a default session if none exists yet and bind it to the agent.
    ///     Called once at app startup. Reads the fresh <see cref="CommonConfig" />
    ///     from disk so the wizard's saved provider/model take effect.
    /// </summary>
    public async Task EnsureDefaultSessionAsync()
    {
        if (_router.ActiveContext is not null) return;

        var createResult = await _factory.CreateDefaultAsync().ConfigureAwait(false);
        if (createResult.IsFailure) return;

        var session = createResult.Value;

        var ctx = _router.GetOrCreateContext(session);
        _router.ActiveContext = ctx;
        RefreshGitInfo(session.Id, session.Directory);
        _status.SetStatus(session.Id, SessionStatus.Idle);
        RebindChatViewModel(ctx);

        if (!await _switcher.OpenAsync(session, ctx.Store).ConfigureAwait(false)) return;
        ctx.StoreWasHydrated = true;
    }

    /// <summary>
    ///     Rebind the active session to the freshly-loaded
    ///     <see cref="CommonConfig" /> values. Called by <c>App.axaml.cs</c>
    ///     after the onboarding wizard saves a new config.
    /// </summary>
    public async Task RebindFromCommonConfigAsync()
    {
        if (_router.ActiveContext is null)
        {
            await EnsureDefaultSessionAsync().ConfigureAwait(false);
            return;
        }

        await AbortRunningAgentAsync().ConfigureAwait(false);

        var agentDef = _agents.GetAllAgents().FirstOrDefault(a => a.Name.Value == "code")
                       ?? _agents.GetAllAgents().FirstOrDefault()
                       ?? throw new InvalidOperationException("No agents registered.");

        (string? providerId, string? modelId) = await _factory.ResolveProviderModelFromConfigAsync().ConfigureAwait(false);
        if (string.IsNullOrEmpty(providerId) || string.IsNullOrEmpty(modelId))
        {
            _logger.LogInformation("RebindFromCommonConfig: no provider/model in config, keeping current agent");
            return;
        }

        agentDef = agentDef.WithModel(modelId, providerId);
        var session = _router.ActiveContext.Session with { ProviderId = providerId, Model = modelId };
        _router.ActiveContext.Session = session;
        _agent.Initialize(session, agentDef);
        _router.ActiveContext.Store.Dispatch(new ChatAppMsg.ConfigureRuntime(agentDef.Model, agentDef.ProviderId, agentDef.Name.Value));
        _logger.LogInformation("Rebound session {Id} to provider={Provider} model={Model}",
            session.Id, providerId, modelId);
    }

    /// <summary>
    ///     Create a new session with the given agent/model and switch to it.
    ///     The previously-active session's agent is <b>not</b> aborted —
    ///     it continues running in the background and its events keep
    ///     flowing into its own UiStore.
    /// </summary>
    /// <returns>The new active session, or a failure carrying the cause.</returns>
    public async Task<Result<Session>> NewSessionAsync(string? agentName = null, string? providerId = null, string? modelId = null, string? workingDirectory = null)
    {
        var createResult = await _factory.CreateNewAsync(agentName, providerId, modelId, workingDirectory).ConfigureAwait(false);
        if (createResult.IsFailure) return createResult;

        var session = createResult.Value;
        var ctx = _router.GetOrCreateContext(session);
        _router.ActiveContext = ctx;
        ClearTokenUsageForActiveSession();
        RebindChatViewModel(ctx);

        if (!await _switcher.OpenAsync(session, ctx.Store).ConfigureAwait(false))
        {
            _status.SetStatus(session.Id, SessionStatus.Error);
            return Result.Failure<Session>($"Session '{session.Id}' was created but could not be opened.");
        }
        ctx.StoreWasHydrated = true;
        return Result.Success(session);
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
        var ctx = _router.GetOrCreateContext(session);

        if (!ctx.StoreWasHydrated)
        {
            if (!await _switcher.OpenAsync(session, ctx.Store).ConfigureAwait(false)) return false;
            ctx.StoreWasHydrated = true;
        }
        else
        {
            var agentDef = _agents.GetAllAgents().FirstOrDefault(a => a.Name.Value == session.Agent)
                           ?? _agents.GetAllAgents().First()
                           ?? throw new InvalidOperationException("No agents registered.");
            _agent.Initialize(session, agentDef);
            // #89: hydrate-then-swap — same single-AppMsg atomic replay as
            // SessionSwitcher.OpenAsync (see comment there).
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

            ctx.Store.Dispatch(new ChatAppMsg.HydrateSession(
                session.Model, session.ProviderId, session.Agent, lines.ToImmutable()));
        }

        RefreshGitInfo(session.Id, session.Directory);
        _router.ActiveContext = ctx;
        ClearTokenUsageForActiveSession();
        RebindChatViewModel(ctx);

        return true;
    }

    /// <summary>
    ///     Branch the active session — create a new session with the same
    ///     messages and metadata but a new id, then switch to the branch.
    /// </summary>
    /// <returns>The new active branch, or a failure carrying the cause.</returns>
    public async Task<Result<Session>> BranchActiveAsync()
    {
        if (_router.ActiveContext is null) return Result.Failure<Session>("No active session to branch.");
        var branchResult = await _factory.CreateBranchAsync(_router.ActiveContext.Session).ConfigureAwait(false);
        if (branchResult.IsFailure) return branchResult;
        var branch = branchResult.Value;
        if (!await OpenSessionAsync(branch.Id).ConfigureAwait(false))
        {
            _status.SetStatus(branch.Id, SessionStatus.Error);
            return Result.Failure<Session>($"Branch '{branch.Id}' was created but could not be opened.");
        }
        return Result.Success(branch);
    }

    /// <summary>
    ///     Delete the given session. If it is the active session, switches to
    ///     any remaining session (or creates a fresh default). Also parks the
    ///     per-session <see cref="SessionContext" /> for late background events.
    /// </summary>
    public async Task<bool> DeleteSessionAsync(string sessionId)
    {
        var result = await _sessionStore.DeleteAsync(sessionId).ConfigureAwait(false);
        if (result.IsFailure)
        {
            _logger.LogError("Delete session {Id} failed: {Error}", sessionId, result.Error);
            return false;
        }

        _router.ParkContext(sessionId);
        _logger.LogInformation("Deleted session {Id}", sessionId);

        if (_router.ActiveContext?.Session.Id == sessionId)
        {
            _router.ActiveContext = null;
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
    ///     Rename a session by updating its title in the store and
    ///     refreshing the in-memory session record.
    /// </summary>
    public async Task<bool> RenameSessionAsync(string sessionId, string newTitle)
    {
        if (string.IsNullOrWhiteSpace(newTitle))
            return false;

        var sessionResult = await _sessionStore.GetAsync(sessionId).ConfigureAwait(false);
        if (sessionResult.IsFailure)
        {
            _logger.LogWarning("Rename session {Id} failed: {Error}", sessionId, sessionResult.Error);
            return false;
        }

        var updated = sessionResult.Value with { Title = newTitle.Trim(), UpdatedAt = DateTimeOffset.UtcNow };
        var saveResult = await _sessionStore.UpdateAsync(updated).ConfigureAwait(false);
        if (saveResult.IsFailure)
        {
            _logger.LogWarning("Rename session {Id} failed: {Error}", sessionId, saveResult.Error);
            return false;
        }

        _logger.LogInformation("Renamed session {Id} → '{Title}'", sessionId, updated.Title);

        // #89: the store write above is durable, but the live Session records
        // held by this service would keep serving the stale title — update
        // every in-memory copy with the same value just persisted.
        var ctx = _router.GetContext(sessionId);
        if (ctx is not null)
            ctx.Session = updated;
        if (_router.ActiveContext?.Session.Id == sessionId)
            _router.ActiveContext.Session = updated;
        return true;
    }

    /// <summary>
    ///     Clear the UI-only token-usage chart so it tracks only the active
    ///     session's tokens. Called on every session switch (open + new).
    ///     #63 legitimate: optional UI-only dependency — headless hosts never
    ///     register it, so a missing registration is a no-op, not an error.
    /// </summary>
    private void ClearTokenUsageForActiveSession() => _factories.ClearTokenUsage();

    /// <summary>
    ///     Rebind the singleton chat view-model to a different session's
    ///     <see cref="UiStore" />. Delegates to <see cref="IChatViewBinder" />.
    /// </summary>
    private void RebindChatViewModel(SessionContext ctx)
    {
        _chatViewBinder.Rebind(ctx.Store);
        _logger.LogInformation(
            "RebindChatViewModel → session {Id}",
            ctx.Session.Id);
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

        // #49 PR1: single cancellation ingress (null-safe: hosts/tests without
        // the coordinator registered keep the direct cancel).
        // #63 legitimate: optional dependency — plain factory (never the
        // throwing variant), so coordinator-less hosts keep working.
        var coordinator = _factories.ApprovalCoordinator();
        if (coordinator is not null)
        {
            coordinator.RequestCancel(_agent);
        }
        else
        {
            _agent.RequestAbort();
        }

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
