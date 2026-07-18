namespace Harbor.Tui.Abstractions.Panels;

/// <summary>
///     Where a <see cref="TuiPanel" /> docks inside the host renderer's layout tree.
///     Renderers translate this to their own geometry (rows/columns/splits).
/// </summary>
public enum TuiPanelPlacement : byte
{
    /// <summary>Docked above the chat history (full width, fixed height).</summary>
    Top,

    /// <summary>Docked below the chat history, above the footer (full width, fixed height).</summary>
    Bottom,

    /// <summary>Docked to the left of the chat history (fixed width, full height).</summary>
    Left,

    /// <summary>Docked to the right of the chat history (fixed width, full height).</summary>
    Right,

    /// <summary>Replaces/overlays the chat history region (modal-like).</summary>
    Center,

    /// <summary>Opened in a tabbed region alongside other panels of the same placement.</summary>
    FloatingTab
}

/// <summary>
///     Runtime visibility / persistence state of a registered panel.
///     Driven exclusively by <c>UiReducer</c> through <c>UiMsg.TogglePanel</c> /
///     <c>UiMsg.FocusPanel</c> so it survives replay and stays identical across renderers.
/// </summary>
public enum TuiPanelState : byte
{
    /// <summary>Not currently rendered. Hotkey will make it <see cref="Visible" />.</summary>
    Hidden,

    /// <summary>Rendered but does not own focus.</summary>
    Visible,

    /// <summary>Rendered AND owns keyboard focus (keystrokes route to its <c>OnKey</c>).</summary>
    Focused,

    /// <summary>Rendered, kept open even when the user cycles focus away (sticky).</summary>
    Pinned
}

/// <summary>
///     Immutable description of a panel: identity, default placement, and a hint
///     for its preferred size (rows for Top/Bottom, columns for Left/Right).
/// </summary>
/// <param name="Id">Stable, lowercase panel id (e.g. <c>"todo-list"</c>).</param>
/// <param name="Title">Human-readable title shown in the panel's tab/border.</param>
/// <param name="Placement">Where the panel docks by default.</param>
/// <param name="PreferredSize">
///     Desired size: rows for Top/Bottom, columns for Left/Right. <c>0</c> = use the
///     provider's <c>DefaultSize</c>.
/// </param>
public sealed record TuiPanel(
    string Id,
    string Title,
    TuiPanelPlacement Placement,
    int PreferredSize = 0);
