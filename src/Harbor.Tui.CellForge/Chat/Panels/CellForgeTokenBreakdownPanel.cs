using Harbor.Ui.Framework.Panels;
using Harbor.Ui.Framework.Projection;
using Harbor.Ui.Framework.State;

namespace Harbor.Tui.CellForge.Panels;

/// <summary>
///     Cell-native token-breakdown panel: cumulative <see cref="UiState.Cost"/>
///     totals with <c>█</c> / <c>░</c> bars and K/M formatting.
///     Non-interactive.
/// </summary>
public sealed class CellForgeTokenBreakdownPanel : CellForgePanelBase
{
    /// <inheritdoc />
    public override string Id => "token-breakdown";

    /// <inheritdoc />
    public override string Title => "Token Breakdown";

    /// <inheritdoc />
    public override TuiPanelPlacement DefaultPlacement => TuiPanelPlacement.Bottom;

    /// <inheritdoc />
    public override int DefaultSize => 10;

    /// <inheritdoc />
    public override object? Build(PanelContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        return PanelText.Clip(
            PanelRows.TokenRows(ctx.State.Cost.TokensIn, ctx.State.Cost.TokensOut, ctx.State.Cost.CostUsd, ctx.Width),
            ctx.Width,
            ctx.Height);
    }
}
