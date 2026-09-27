using CSharpFunctionalExtensions;
using Harbor.Abstractions.Models;
namespace Harbor.Ui.Framework.Sessions;
/// <summary>
///     Session lifecycle: create/open/branch/delete/rename plus git refresh
///     and config rebind. Implemented by <see cref="SessionLifecycleService" />
///     (and forwarded by the <see cref="SessionManager" /> facade) — depend
///     on this instead of the full <see cref="ISessionManager" /> when only
///     lifecycle operations are needed (ISP: issue #189).
/// </summary>
public interface ISessionLifecycle
{
    /// <summary>Refresh git info for a session.</summary>
    void RefreshGitInfo(string sessionId, string directory);

    /// <summary>Create a default session if none exists yet and bind it to the agent.</summary>
    Task EnsureDefaultSessionAsync();

    /// <summary>Rebind the active session to freshly-loaded CommonConfig values.</summary>
    Task RebindFromCommonConfigAsync();

    /// <summary>Create a new session and switch to it.</summary>
    /// <returns>The new active session, or a failure carrying the cause.</returns>
    Task<Result<Session>> NewSessionAsync(string? agentName = null, string? providerId = null, string? modelId = null, string? workingDirectory = null);

    /// <summary>Open (switch to) an existing session.</summary>
    Task<bool> OpenSessionAsync(string sessionId);

    /// <summary>Branch the active session.</summary>
    /// <returns>The new active branch, or a failure carrying the cause.</returns>
    Task<Result<Session>> BranchActiveAsync();

    /// <summary>Delete the given session.</summary>
    Task<bool> DeleteSessionAsync(string sessionId);

    /// <summary>Rename a session.</summary>
    Task<bool> RenameSessionAsync(string sessionId, string newTitle);
}
