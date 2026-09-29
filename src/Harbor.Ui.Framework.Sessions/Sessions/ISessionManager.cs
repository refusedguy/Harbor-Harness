namespace Harbor.Ui.Framework.Sessions;
using Harbor.Ui.Framework.Panels;
/// <summary>
///     Facade that owns the active session and delegates creation, switching,
///     git-tracking, and status-tracking to dedicated services. Composite of
///     <see cref="ISessionQueries" />, <see cref="ISessionLifecycle" />, and
///     <see cref="ISessionStatusTracker" /> — prefer one of the narrow
///     interfaces for new dependencies (ISP: issue #189).
/// </summary>
/// <remarks>
///     Also an <see cref="IPanelSessionGateway" /> (#470): framework panels read
///     per-session facts through that narrow projection, so a host that
///     registers an <see cref="ISessionManager" /> can hand the same instance to
///     <c>PanelServices</c> without an adapter or a service locator.
/// </remarks>
public interface ISessionManager : ISessionQueries, ISessionLifecycle, ISessionStatusTracker, IPanelSessionGateway
{
}
