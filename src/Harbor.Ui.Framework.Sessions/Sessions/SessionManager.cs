using CSharpFunctionalExtensions;
using Harbor.Abstractions.Models;
using Harbor.Ui.Framework.Panels;
using Harbor.Ui.Framework.Services;
namespace Harbor.Ui.Framework.Sessions;
/// <summary>
///     Facade that owns the active session and delegates creation, switching,
///     git-tracking, and status-tracking to dedicated services. The sidebar
///     (<see cref="SessionListViewModel" />) drives this — New / Open / Branch /
///     Delete operations flow through here so the agent + UiStore stay in sync.
/// </summary>
/// <remarks>
///     <para>
///         Thin delegation only (issue #189) — all orchestration lives in
///         <see cref="SessionLifecycleService" />, all context routing in
///         <see cref="SessionEventRouter" />, all status tracking in
///         <see cref="SessionStatusService" />. No Service Locator: optional
///         host-only collaborators arrive as Func-factories via
///         <see cref="SessionOptionalFactories" />.
///     </para>
///     <para>
///         <b>Per-session UiStore (concurrent agents):</b> each open session
///         has its own <see cref="SessionContext" /> / <see cref="UiStore" />
///         held by the <see cref="SessionEventRouter" />. See
///         <see cref="SessionLifecycleService" /> for the full decomposition.
///     </para>
/// </remarks>
public sealed class SessionManager : ISessionManager
{
    private readonly SessionGitTracker _gitTracker;
    private readonly SessionLifecycleService _lifecycle;
    private readonly SessionEventRouter _router;
    private readonly SessionStatusService _status;

    /// <summary>Construct a <see cref="SessionManager" /> facade.</summary>
    public SessionManager(
        SessionEventRouter router,
        SessionLifecycleService lifecycle,
        SessionStatusService status,
        SessionGitTracker gitTracker)
    {
        _router = router;
        _lifecycle = lifecycle;
        _status = status;
        _gitTracker = gitTracker;
    }

    /// <summary>The active session, or null if none.</summary>
    public Session? Active => _router.Active;

    /// <summary>
    ///     The active <see cref="SessionContext" /> (holds the active session
    ///     + its UiStore + status + git info), or null if none. The ChatViewModel
    ///     is bound to <see cref="SessionContext.Store" /> of this context.
    /// </summary>
    public SessionContext? ActiveContext => _router.ActiveContext;

    /// <summary>
    ///     Raised whenever a session's status changes. Forwards from
    ///     <see cref="SessionStatusTracker.StatusChanged" /> so subscribers
    ///     don't need to know about the tracker decomposition.
    /// </summary>
    public event Action<string, SessionStatus>? StatusChanged
    {
        add => _status.StatusChanged += value;
        remove => _status.StatusChanged -= value;
    }

    /// <summary>
    ///     Raised whenever a session's message count is pushed. Forwards from
    ///     <see cref="SessionStatusTracker.MessageCountChanged" />.
    /// </summary>
    public event Action<string, int>? MessageCountChanged
    {
        add => _status.MessageCountChanged += value;
        remove => _status.MessageCountChanged -= value;
    }

    /// <summary>
    ///     Look up a <see cref="SessionContext" /> by session id. Forwards to
    ///     <see cref="SessionEventRouter.GetContext" /> (live context with
    ///     tombstoned fallback for late background events, #89).
    /// </summary>
    /// <param name="sessionId">The session id to look up.</param>
    /// <returns>The <see cref="SessionContext" />, or null.</returns>
    public SessionContext? GetContext(string sessionId) => _router.GetContext(sessionId);

    /// <summary>Get the status of a session.</summary>
    public SessionStatus GetStatus(string sessionId) => _status.GetStatus(sessionId);

    /// <summary>Set the status of a session (forwards to <see cref="SessionStatusService" />).</summary>
    public void SetStatus(string sessionId, SessionStatus status) =>
        _status.SetStatus(sessionId, status);

    /// <summary>Push a fresh message count for a session (forwards to <see cref="SessionStatusService" />).</summary>
    /// <param name="sessionId">The session id.</param>
    /// <param name="count">The new message count.</param>
    public void NotifyMessageCount(string sessionId, int count) =>
        _status.NotifyMessageCount(sessionId, count);

    /// <summary>Get git info for a session's working directory (forwards to <see cref="SessionGitTracker" />).</summary>
    public GitSessionInfo GetGitInfo(string sessionId) =>
        _gitTracker.Get(sessionId);

    /// <summary>Refresh git info for a session (forwards to <see cref="SessionLifecycleService" />).</summary>
    public void RefreshGitInfo(string sessionId, string directory) =>
        _lifecycle.RefreshGitInfo(sessionId, directory);

    /// <summary>
    ///     Create a default session if none exists yet and bind it to the agent.
    ///     Called once at app startup. Forwards to <see cref="SessionLifecycleService" />.
    /// </summary>
    public Task EnsureDefaultSessionAsync() => _lifecycle.EnsureDefaultSessionAsync();

    /// <summary>
    ///     Rebind the active session to the freshly-loaded
    ///     <see cref="CommonConfig" /> values. Forwards to <see cref="SessionLifecycleService" />.
    /// </summary>
    public Task RebindFromCommonConfigAsync() => _lifecycle.RebindFromCommonConfigAsync();

    /// <summary>
    ///     Create a new session with the given agent/model and switch to it.
    ///     Forwards to <see cref="SessionLifecycleService" />.
    /// </summary>
    /// <returns>The new active session, or a failure carrying the cause.</returns>
    public Task<Result<Session>> NewSessionAsync(string? agentName = null, string? providerId = null, string? modelId = null, string? workingDirectory = null) =>
        _lifecycle.NewSessionAsync(agentName, providerId, modelId, workingDirectory);

    /// <summary>
    ///     Open (switch to) an existing session. Forwards to <see cref="SessionLifecycleService" />.
    /// </summary>
    public Task<bool> OpenSessionAsync(string sessionId) => _lifecycle.OpenSessionAsync(sessionId);

    /// <inheritdoc />
    public Task<bool> OpenPanelSessionAsync(string sessionId) => _lifecycle.OpenSessionAsync(sessionId);

    /// <summary>
    ///     Branch the active session. Forwards to <see cref="SessionLifecycleService" />.
    /// </summary>
    /// <returns>The new active branch, or a failure carrying the cause.</returns>
    public Task<Result<Session>> BranchActiveAsync() => _lifecycle.BranchActiveAsync();

    /// <summary>
    ///     Delete the given session. Forwards to <see cref="SessionLifecycleService" />.
    /// </summary>
    public Task<bool> DeleteSessionAsync(string sessionId) => _lifecycle.DeleteSessionAsync(sessionId);

    /// <summary>
    ///     Rename a session. Forwards to <see cref="SessionLifecycleService" />.
    /// </summary>
    public Task<bool> RenameSessionAsync(string sessionId, string newTitle) =>
        _lifecycle.RenameSessionAsync(sessionId, newTitle);

    // ── IPanelSessionGateway (#470) ──────────────────────────────────────
    // The adapter that lets framework panels (which live in
    // Harbor.Ui.Framework.State and therefore cannot name ISessionManager)
    // read per-session facts without a service locator. The precedence chains
    // below reproduce exactly what the panels used to spell out against the
    // session-context and cached-git lookups, so no seeded row changes.

    /// <inheritdoc />
    public string? GetDirectory(string sessionId) => _router.GetContext(sessionId)?.Session.Directory;

    /// <inheritdoc />
    public string? GetStatusText(string sessionId) => _router.GetContext(sessionId)?.StatusText;

    /// <inheritdoc />
    public string? GetBranch(string sessionId)
    {
        var ctx = _router.GetContext(sessionId);
        return _gitTracker.Get(sessionId).Branch ?? ctx?.Session.GitBranch ?? ctx?.GitBranch;
    }

    /// <inheritdoc />
    public bool GetIsDirty(string sessionId)
    {
        var ctx = _router.GetContext(sessionId);
        return _gitTracker.Get(sessionId).IsDirty || ctx?.GitIsDirty == true || ctx?.Session.GitIsDirty == true;
    }

    /// <inheritdoc />
    public bool? GetIsSubagent(string sessionId) => _router.GetContext(sessionId)?.Session.IsSubagent();
}
