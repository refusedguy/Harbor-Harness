using Harbor.Ui.Framework.Panels;
using Harbor.Ui.Framework.Projection;
using Harbor.Ui.Framework.State;

namespace Harbor.Tui.CellForge.Panels;

/// <summary>
///     Cell-native todo-list panel: the freshest <c>[ ]</c> / <c>[~]</c> /
///     <c>[x]</c> block parsed from the transcript via
///     <see cref="PanelExtractors.ExtractTodos(UiState)"/>. Non-interactive.
/// </summary>
public sealed class CellForgeTodoListPanel : CellForgePanelBase
{
    /// <inheritdoc />
    public override string Id => "todo-list";

    /// <inheritdoc />
    public override string Title => "Todo List";

    /// <inheritdoc />
    public override TuiPanelPlacement DefaultPlacement => TuiPanelPlacement.Right;

    /// <inheritdoc />
    public override int DefaultSize => 40;

    /// <inheritdoc />
    public override object? Build(PanelContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        return PanelText.Clip(
            PanelRows.TodoRows(PanelExtractors.ExtractTodos(ctx.State), ctx.Width),
            ctx.Width,
            ctx.Height);
    }
}
