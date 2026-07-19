using Harbor.Abstractions.Models;
using Harbor.Ui.Framework.State;

namespace Harbor.App.Avalonia.Services;

/// <summary>
///     Per-session context — holds the session's UiStore (chat state),
///     status, and git info. One SessionContext per session.
///     The active session's UiStore is bound to the ChatViewModel.
///     Background sessions' UiStores continue receiving events
///     so the agent keeps working when the user switches to another session.
/// </summary>
public sealed class SessionContext
{
    /// <summary>The session record.</summary>
    public Session Session { get; set; }

    /// <summary>Per-session UiStore — chat state for THIS session only.</summary>
    public UiStore Store { get; }

    /// <summary>Current status — idle/working/done/error.</summary>
    public SessionStatus Status { get; set; } = SessionStatus.Idle;

    /// <summary>Git branch for the session's working directory (null if not a repo).</summary>
    public string? GitBranch { get; set; }

    /// <summary>Whether the git working tree has uncommitted changes.</summary>
    public bool GitIsDirty { get; set; }

    public SessionContext(Session session)
    {
        Session = session;
        Store = new UiStore();
    }

    /// <summary>Display string for the session row: "main · ~/proj" or "main (dirty) · ~/proj".</summary>
    public string MetaLine
    {
        get
        {
            var branch = GitBranch ?? "no-git";
            var dirty = GitIsDirty ? " (dirty)" : "";
            var dir = System.IO.Path.GetFileName(Session.Directory);
            return $"{branch}{dirty} · {dir}";
        }
    }

    /// <summary>Status as a display string.</summary>
    public string StatusText => Status switch
    {
        SessionStatus.Working => "working",
        SessionStatus.Done => "done",
        SessionStatus.Error => "error",
        SessionStatus.Aborted => "aborted",
        _ => "idle",
    };
}
