namespace Harbor.Ui.Framework.State;

/// <summary>
///     What kind of unread signal a tab carries (#1173, opencode steal).
///     Mirrors <c>SessionTabUnread = "activity" | "error"</c> from opencode's
///     <c>session-tabs-model.ts</c>: a background completion is activity, a
///     failed run is an error, and the two paint in different colours.
/// </summary>
public enum TabUnread
{
    /// <summary>No unread signal — the steady state.</summary>
    None,

    /// <summary>Background activity (a finished turn, new output) the user has not seen.</summary>
    Activity,

    /// <summary>A background failure the user has not seen. Paints as error, not amber.</summary>
    Error,
}
