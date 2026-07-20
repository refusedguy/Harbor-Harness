using Harbor.Abstractions.Models;

namespace Harbor.Ui.Framework.Services;

/// <summary>
///     Tracks <see cref="SessionStatus"/> (idle / working / done / error) per
///     session, keyed on session id. Subscribers (e.g.
///     <see cref="ViewModels.SessionListViewModel"/>) attach to
///     <see cref="StatusChanged"/> to push live updates to the sidebar rows
///     so the status dot colour tracks the agent state in real time instead
///     of being frozen at the last <c>RefreshAsync</c>.
/// </summary>
/// <remarks>
///     Also exposes <see cref="MessageCountChanged"/> so the sidebar can
///     live-update the "N msgs" label without a full
///     <c>RefreshAsync</c> round-trip (Task S2 / Problem 2). Registered as
///     a singleton in <c>AppHost</c>.
/// </remarks>
public sealed class SessionStatusTracker
{
    /// <summary>Per-session status tracking.</summary>
    private readonly Dictionary<string, SessionStatus> _sessionStatuses = new();

    /// <summary>
    ///     Raised whenever <see cref="Set"/> changes a session's status.
    ///     Subscribers use this to push live updates to the sidebar rows.
    /// </summary>
    public event Action<string, SessionStatus>? StatusChanged;

    /// <summary>
    ///     Raised whenever <see cref="NotifyMessageCount"/> pushes a fresh
    ///     message count for a session. Subscribers update the corresponding
    ///     sidebar row's <c>MessageCount</c> in place.
    /// </summary>
    public event Action<string, int>? MessageCountChanged;

    /// <summary>Get the status of a session.</summary>
    /// <param name="sessionId">The session id.</param>
    /// <returns>The current status, or <see cref="SessionStatus.Idle"/> when not tracked.</returns>
    public SessionStatus Get(string sessionId) =>
        _sessionStatuses.TryGetValue(sessionId, out var s) ? s : SessionStatus.Idle;

    /// <summary>
    ///     Set the status of a session and notify subscribers via
    ///     <see cref="StatusChanged"/> so the sidebar can update the row's
    ///     status dot without a full <c>RefreshAsync</c> round-trip.
    /// </summary>
    /// <param name="sessionId">The session id.</param>
    /// <param name="status">The new status.</param>
    public void Set(string sessionId, SessionStatus status)
    {
        if (_sessionStatuses.TryGetValue(sessionId, out var current) && current == status) return;
        _sessionStatuses[sessionId] = status;
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
        MessageCountChanged?.Invoke(sessionId, count);
    }
}
