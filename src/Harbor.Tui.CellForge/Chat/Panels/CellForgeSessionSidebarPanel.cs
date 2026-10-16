using Harbor.Ui.Framework.Overlays;
using Ensure = Harbor.Abstractions.Extensions.Ensure;
using Harbor.Ui.Framework.Panels;
using Harbor.Ui.Framework.Projection;
using Harbor.Ui.Framework.State;

namespace Harbor.Tui.CellForge.Panels;

/// <summary>
///     Cell-native session sidebar panel: lists all known sessions from
///     <see cref="UiState.Chat.Sessions"/> with the active session highlighted.
///     Non-interactive (read-only view).
///     Search behaviour (instant local filter + debounced server tier, scope
///     switch, pin/rename/delete) lives in <see cref="SessionListSearchModel"/>
///     (#1178); this panel exposes its debounce so host wiring reads one value.
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

    /// <summary>
    ///     Server-search debounce (ms) for the session-list dialog. Passthrough
    ///     of <see cref="SessionListSearchModel.ServerDebounceMs"/> so host
    ///     wiring and the pure model can never drift apart.
    /// </summary>
    internal static int SearchDebounceMs => SessionListSearchModel.ServerDebounceMs;

    /// <inheritdoc />
    public override object? Build(PanelContext ctx)
    {
        Ensure.NotNull(ctx);
        return PanelText.Clip(
            PanelRows.SessionRows(ctx.State.Chat.Sessions, ctx.State.Chat.ActiveSessionId, ctx.State.Chat.IsLoading, ctx.Width, ctx.Height),
            ctx.Width,
            ctx.Height);
    }
}
