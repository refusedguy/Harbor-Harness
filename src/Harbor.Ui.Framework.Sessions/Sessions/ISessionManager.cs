namespace Harbor.Ui.Framework.Sessions;
/// <summary>
///     Facade that owns the active session and delegates creation, switching,
///     git-tracking, and status-tracking to dedicated services. Composite of
///     <see cref="ISessionQueries" />, <see cref="ISessionLifecycle" />, and
///     <see cref="ISessionStatusTracker" /> — prefer one of the narrow
///     interfaces for new dependencies (ISP: issue #189).
/// </summary>
public interface ISessionManager : ISessionQueries, ISessionLifecycle, ISessionStatusTracker
{
}
