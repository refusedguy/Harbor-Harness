using Harbor.Ui.Framework.Panels;
using Harbor.Ui.Framework.Projection;
using Harbor.Ui.Framework.State;

namespace Harbor.Tui.CellForge.Panels;

/// <summary>
///     Cell-native session sidebar panel: lists all known sessions from
///     <see cref="UiState.Sessions"/> with the active session highlighted.
///     Non-interactive (read-only view).
/// </summary>
public sealed class CellForgeSessionSidebarPanel : CellForgePanelBase
{
    /// <inheritdoc />
    public override string Id => "session-sidebar";

    /// <inheritdoc />
    public override string Title => "Sessions";

    /// <inheritdoc />
    public override TuiPanelPlacement DefaultPlacement => TuiPanelPlacement.Left;

    /// <inheritdoc />
    public override int DefaultSize => 32;

    /// <inheritdoc />
    public override object? Build(PanelContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        return PanelText.Clip(
            PanelRows.SessionRows(ctx.State.Sessions, ctx.State.ActiveSessionId, ctx.State.IsLoading, ctx.Width, ctx.Height),
            ctx.Width,
            ctx.Height);
    }
}
