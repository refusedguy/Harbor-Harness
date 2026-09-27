using Harbor.Abstractions.Models;
namespace Harbor.Ui.Framework.Sessions;
/// <summary>
///     Per-session status tracking: get/set status plus message-count pushes.
///     Implemented by <see cref="SessionStatusService" /> (and forwarded by
///     the <see cref="SessionManager" /> facade) — depend on this instead of
///     the full <see cref="ISessionManager" /> when only status is needed
///     (ISP: issue #189).
/// </summary>
public interface ISessionStatusTracker
{
    /// <summary>Get the status of a session.</summary>
    SessionStatus GetStatus(string sessionId);

    /// <summary>Set the status of a session.</summary>
    void SetStatus(string sessionId, SessionStatus status);

    /// <summary>Push a fresh message count for a session.</summary>
    void NotifyMessageCount(string sessionId, int count);

    /// <summary>
    ///     Raised whenever a session's status changes.
    /// </summary>
    event Action<string, SessionStatus>? StatusChanged;

    /// <summary>
    ///     Raised whenever a session's message count is pushed.
    /// </summary>
    event Action<string, int>? MessageCountChanged;
}
