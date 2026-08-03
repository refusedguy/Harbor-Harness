using Harbor.Abstractions.Models;
using Harbor.Ui.Framework.State;
namespace Harbor.Ui.Framework.Sessions;
/// <summary>
///     Per-session context — holds the session's UiStore (chat state),
///     status, and git info. One SessionContext per session.
///     The active session's UiStore is bound to the ChatViewModel.
///     Background sessions' UiStores continue receiving events
///     so the agent keeps working when the user switches to another session.
/// </summary>
public sealed class SessionContext
{

    public SessionContext(Session session)
    {
        Session = session;
        Store = new UiStore();
    }
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

    /// <summary>Whether the per-session <see cref="Store" /> has been hydrated with
    ///     the persisted message history (or restored from a previous visit).
    ///     False on first sight of the session — <see cref="SessionSwitcher.OpenAsync" />
    ///     replays the message history into the store and sets this to true.
    ///     Subsequent switches to this session skip the replay because the
    ///     in-memory store already has the state.
    /// </summary>
    public bool StoreWasHydrated { get; set; }

    /// <summary>Display string for the session row: "main · ~/proj" or "main (dirty) · ~/proj".</summary>
    public string MetaLine
    {
        get
        {
            string branch = GitBranch ?? "no-git";
            string dirty = GitIsDirty ? " (dirty)" : "";
            string dir = Path.GetFileName(Session.Directory);
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
        _ => "idle"
    };
}
