namespace Harbor.Ui.Framework.Sessions;

/// <summary>
///     The session-cluster collaborators a lifecycle run needs (issue #486
///     finding 4): routing, creation, switching, git tracking and status.
///     Five members — inserting one more session collaborator rebinds only
///     this scope's call sites, never the service's.
/// </summary>
public sealed record SessionLifecycleScope(
    SessionEventRouter Router,
    SessionFactory Factory,
    SessionSwitcher Switcher,
    SessionGitTracker GitTracker,
    SessionStatusService Status);
