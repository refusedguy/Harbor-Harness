using Harbor.Abstractions.Models;
using Harbor.Ui.Framework.Services;
namespace Harbor.Ui.Framework.Sessions;
/// <summary>
///     Read-only session queries: the active session, per-session contexts
///     for event routing, and cached git info. Implemented by
///     <see cref="SessionManager" /> (facade) — depend on this instead of
///     the full <see cref="ISessionManager" /> when no lifecycle or status
///     writes are needed (ISP: issue #189).
/// </summary>
public interface ISessionQueries
{
    /// <summary>The active session, or null if none.</summary>
    Session? Active { get; }

    /// <summary>
    ///     The active <see cref="SessionContext" /> (holds the active session
    ///     + its UiStore + status + git info), or null if none. Renderers
    ///     bind to <see cref="SessionContext.Store" /> of this context and
    ///     fall back to their private store when it is null.
    /// </summary>
    SessionContext? ActiveContext { get; }

    /// <summary>Look up a session context by session id.</summary>
    SessionContext? GetContext(string sessionId);

    /// <summary>Get git info for a session's working directory.</summary>
    GitSessionInfo GetGitInfo(string sessionId);
}
