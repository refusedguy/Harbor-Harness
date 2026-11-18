using System.Collections.Concurrent;
using Harbor.Abstractions.Models;
using Harbor.Ui.Framework.State;
namespace Harbor.Ui.Framework.Services;
/// <summary>
///     Projects the <see cref="SessionStatus" /> the reducer already decided in
///     <see cref="UiState.Chat" /> onto the per-session status events the sidebar
///     consumes. There is no status authority here: <see cref="Bind" /> attaches
///     this singleton to the active session's <see cref="UiStore" />, and every
///     transition is read straight out of the snapshot the store publishes.
///     Subscribers (e.g. <see cref="ViewModels.SessionListViewModel" />) attach
///     to <see cref="StatusChanged" /> to push live updates to the sidebar rows
///     so the status dot colour tracks the agent state in real time instead
///     of being frozen at the last <c>RefreshAsync</c>.
/// </summary>
/// <remarks>
///     Also exposes <see cref="MessageCountChanged" /> so the sidebar can
///     live-update the "N msgs" label without a full
///     <c>RefreshAsync</c> round-trip (Task S2 / Problem 2). Registered as
///     a singleton in <c>AppHost</c>.
/// </remarks>
public sealed class SessionStatusTracker
{
    /// <summary>
    ///     Per-session message counts (a display projection, not a status authority).
    ///     This tracker is a singleton and <see cref="NotifyMessageCount" /> is
    ///     reachable both from the UI frame and from store notifications on pool
    ///     threads, so the map must tolerate concurrent writers (#2550).
    /// </summary>
    private readonly ConcurrentDictionary<string, int> _messageCounts = new();

    /// <summary>The store whose state this projection follows.</summary>
    private UiStore? _boundStore;

    /// <summary>The session the bound store belongs to.</summary>
    private string? _boundSessionId;

    /// <summary>Last applied store revision — stale notifications are dropped.</summary>
    private long _lastRevision = -1;

    /// <summary>Last status published over <see cref="StatusChanged" />, for de-duplication.</summary>
    private SessionStatus _publishedStatus = SessionStatus.Idle;

    /// <summary>
    ///     Raised whenever the bound session's status changes.
    ///     Subscribers use this to push live updates to the sidebar rows.
    /// </summary>
    public event Action<string, SessionStatus>? StatusChanged;

    /// <summary>
    ///     Raised whenever <see cref="NotifyMessageCount" /> pushes a fresh
    ///     message count for a session. Subscribers update the corresponding
    ///     sidebar row's <c>MessageCount</c> in place.
    /// </summary>
    public event Action<string, int>? MessageCountChanged;

    /// <summary>
    ///     Attach this projection to the store of the active session. Rebinding
    ///     unsubscribes the previous store first, so a background agent's store
    ///     cannot keep publishing into a row that now belongs to another session.
    /// </summary>
    /// <param name="store">The session's store.</param>
    /// <param name="sessionId">The id of the session that owns the store.</param>
    public void Bind(UiStore store, string sessionId)
    {
        ArgumentNullException.ThrowIfNull(store);
        if (ReferenceEquals(_boundStore, store)
            && string.Equals(_boundSessionId, sessionId, StringComparison.Ordinal))
        {
            return;
        }

        if (_boundStore is not null)
            _boundStore.Changed -= OnStoreChanged;

        _boundStore = store;
        _boundSessionId = sessionId;
        _lastRevision = -1;
        _publishedStatus = SessionStatus.Idle;
        _boundStore.Changed += OnStoreChanged;

        PublishFrom(store.State);
    }

    /// <summary>Read the status the reducer decided out of each published snapshot.</summary>
    private void OnStoreChanged(object? sender, UiStateChangedEventArgs e)
    {
        if (e.IsStale(_lastRevision)) return;
        PublishFrom(e.State);
    }

    /// <summary>
    ///     Fan one snapshot out to both live views. The status is read from
    ///     <c>UiState.Chat.SessionStatus</c> — the value the reducer decided —
    ///     never recomputed from the transcript here.
    /// </summary>
    private void PublishFrom(UiState state)
    {
        string? sessionId = _boundSessionId;
        if (sessionId is null) return;

        _lastRevision = state.Revision;
        PublishStatus(sessionId, state.Chat.SessionStatus);
        NotifyMessageCount(sessionId, state.Chat.Lines.Length);
    }

    /// <summary>Get the status of a session — the bound store's decision, or Idle when untracked.</summary>
    /// <param name="sessionId">The session id.</param>
    /// <returns>The current status, or <see cref="SessionStatus.Idle" /> when not tracked.</returns>
    public SessionStatus Get(string sessionId) =>
        _boundStore is not null && string.Equals(_boundSessionId, sessionId, StringComparison.Ordinal)
            ? _boundStore.State.Chat.SessionStatus
            : SessionStatus.Idle;

    /// <summary>
    ///     Publish a status change to subscribers. Statuses decided by a run
    ///     arrive through the store subscription in <see cref="PublishFrom" />;
    ///     this entry point remains for callers that report a failure outside
    ///     the store (a session that could not be opened, for example).
    /// </summary>
    /// <param name="sessionId">The session id.</param>
    /// <param name="status">The new status.</param>
    public void Set(string sessionId, SessionStatus status) => PublishStatus(sessionId, status);

    /// <summary>Raise <see cref="StatusChanged" /> once per actual change.</summary>
    private void PublishStatus(string sessionId, SessionStatus status)
    {
        if (!string.Equals(_boundSessionId, sessionId, StringComparison.Ordinal)) return;
        if (_publishedStatus == status) return;
        _publishedStatus = status;
        StatusChanged?.Invoke(sessionId, status);
    }

    /// <summary>
    ///     Push a fresh message count for a session. Subscribers update the
    ///     sidebar row's <c>MessageCount</c> in place.
    /// </summary>
    /// <param name="sessionId">The session id.</param>
    /// <param name="count">The new message count.</param>
    public void NotifyMessageCount(string sessionId, int count)
    {
        if (_messageCounts.TryGetValue(sessionId, out var current) && current == count) return;
        _messageCounts[sessionId] = count;
        MessageCountChanged?.Invoke(sessionId, count);
    }
}
