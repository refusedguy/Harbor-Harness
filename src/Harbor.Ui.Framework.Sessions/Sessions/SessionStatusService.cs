using Harbor.Abstractions.Models;
using Harbor.Ui.Framework.Services;
namespace Harbor.Ui.Framework.Sessions;
/// <summary>
///     Per-session status + message-count tracking behind the narrow
///     <see cref="ISessionStatusTracker" /> contract. Extracted from the
///     <see cref="SessionManager" /> facade (issue #189): a thin adapter
///     over the shared <see cref="SessionStatusTracker" /> singleton so
///     status subscribers never need the full session manager.
/// </summary>
public sealed class SessionStatusService : ISessionStatusTracker
{
    private readonly SessionStatusTracker _tracker;

    /// <summary>Construct a <see cref="SessionStatusService" />.</summary>
    public SessionStatusService(SessionStatusTracker tracker)
    {
        _tracker = tracker;
    }

    /// <summary>
    ///     Raised whenever a session's status changes. Forwards from
    ///     <see cref="SessionStatusTracker.StatusChanged" />.
    /// </summary>
    public event Action<string, SessionStatus>? StatusChanged
    {
        add => _tracker.StatusChanged += value;
        remove => _tracker.StatusChanged -= value;
    }

    /// <summary>
    ///     Raised whenever a session's message count is pushed. Forwards from
    ///     <see cref="SessionStatusTracker.MessageCountChanged" />.
    /// </summary>
    public event Action<string, int>? MessageCountChanged
    {
        add => _tracker.MessageCountChanged += value;
        remove => _tracker.MessageCountChanged -= value;
    }

    /// <summary>Get the status of a session.</summary>
    public SessionStatus GetStatus(string sessionId) => _tracker.Get(sessionId);

    /// <summary>Set the status of a session (forwards to <see cref="SessionStatusTracker" />).</summary>
    public void SetStatus(string sessionId, SessionStatus status) =>
        _tracker.Set(sessionId, status);

    /// <summary>Push a fresh message count for a session (forwards to <see cref="SessionStatusTracker" />).</summary>
    /// <param name="sessionId">The session id.</param>
    /// <param name="count">The new message count.</param>
    public void NotifyMessageCount(string sessionId, int count) =>
        _tracker.NotifyMessageCount(sessionId, count);
}
