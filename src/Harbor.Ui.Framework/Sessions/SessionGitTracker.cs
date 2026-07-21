using Harbor.Ui.Framework.Services;
namespace Harbor.Ui.Framework.Sessions;
/// <summary>
///     Tracks git status (branch + dirty flag) per session, keyed on session
///     id. Refreshed on every session switch via
///     <see cref="SessionManager.OpenSessionAsync" /> /
///     <see cref="SessionManager.EnsureDefaultSessionAsync" />. Decoupled
///     from <see cref="SessionManager" /> so the tracker can be unit-tested
///     in isolation (and so the git refresh can be mocked in tests that
///     don't have a real git binary on PATH).
/// </summary>
public sealed class SessionGitTracker
{
    /// <summary>Per-session git info (branch + dirty flag).</summary>
    private readonly Dictionary<string, (string? Branch, bool IsDirty)> _gitInfo = new();

    /// <summary>
    ///     Get the cached git info for a session.
    /// </summary>
    /// <param name="sessionId">The session id.</param>
    /// <returns>The cached (branch, isDirty) tuple, or (null, false) when no info is cached.</returns>
    public (string? Branch, bool IsDirty) Get(string sessionId)
    {
        if (_gitInfo.TryGetValue(sessionId, out var info))
            return info;
        return (null, false);
    }

    /// <summary>
    ///     Refresh the cached git info for a session by querying the
    ///     <see cref="GitService" /> for the session's working directory.
    ///     No-op when <paramref name="gitService" /> is null (no git binary
    ///     available — e.g. in unit tests).
    /// </summary>
    /// <param name="sessionId">The session id.</param>
    /// <param name="directory">The session's working directory.</param>
    /// <param name="gitService">The git service (may be null).</param>
    public void Refresh(string sessionId, string directory, GitService? gitService)
    {
        if (gitService is null) return;
        var info = gitService.GetGitStatus(directory);
        _gitInfo[sessionId] = info;
    }
}
