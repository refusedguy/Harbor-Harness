using Harbor.Abstractions.Models;
using Harbor.Abstractions.Sessions;
namespace Harbor.Ui.Framework.Sessions;
/// <summary>
///     Facade that owns the active session and delegates creation, switching,
///     git-tracking, and status-tracking to dedicated services.
/// </summary>
public interface ISessionManager
{
    /// <summary>The active session, or null if none.</summary>
    Session? Active { get; }

    /// <summary>Look up a session context by session id.</summary>
    SessionContext? GetContext(string sessionId);

    /// <summary>Get the status of a session.</summary>
    SessionStatus GetStatus(string sessionId);

    /// <summary>Set the status of a session.</summary>
    void SetStatus(string sessionId, SessionStatus status);

    /// <summary>Push a fresh message count for a session.</summary>
    void NotifyMessageCount(string sessionId, int count);

    /// <summary>Get git info for a session's working directory.</summary>
    (string? Branch, bool IsDirty) GetGitInfo(string sessionId);

    /// <summary>Refresh git info for a session.</summary>
    void RefreshGitInfo(string sessionId, string directory);

    /// <summary>Create a default session if none exists yet and bind it to the agent.</summary>
    Task EnsureDefaultSessionAsync();

    /// <summary>Rebind the active session to freshly-loaded CommonConfig values.</summary>
    Task RebindFromCommonConfigAsync();

    /// <summary>Create a new session and switch to it.</summary>
    Task<Session?> NewSessionAsync(string? agentName = null, string? providerId = null, string? modelId = null, string? workingDirectory = null);

    /// <summary>Open (switch to) an existing session.</summary>
    Task<bool> OpenSessionAsync(string sessionId);

    /// <summary>Branch the active session.</summary>
    Task<Session?> BranchActiveAsync();

    /// <summary>Delete the given session.</summary>
    Task<bool> DeleteSessionAsync(string sessionId);

    /// <summary>Rename a session.</summary>
    Task<bool> RenameSessionAsync(string sessionId, string newTitle);

    /// <summary>
    ///     Raised whenever a session's status changes.
    /// </summary>
    event Action<string, SessionStatus>? StatusChanged;

    /// <summary>
    ///     Raised whenever a session's message count is pushed.
    /// </summary>
    event Action<string, int>? MessageCountChanged;
}
