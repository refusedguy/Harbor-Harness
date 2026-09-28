using Harbor.Ui.Framework.Panels;
using Harbor.Ui.Framework.Projection;
using Harbor.Ui.Framework.State;

namespace Harbor.Tui.CellForge.Panels;

/// <summary>
///     Cell-native diff-preview panel: recent <c>edit</c> / <c>write</c> /
///     <c>read</c> / <c>patch</c> tool calls paired with their results via
///     <see cref="PanelExtractors.ExtractRecentChanges(UiState, int)"/>. One header
///     row per change (<c>tool-icon ok-icon path</c>) plus up to 4 diff body lines.
///     Non-interactive.
/// </summary>
public sealed class CellForgeDiffPreviewPanel : CellForgePanelBase
{
    /// <inheritdoc />
    public override string Id => "diff-preview";

    /// <inheritdoc />
    public override string Title => "Diff Preview";

    /// <inheritdoc />
    public override TuiPanelPlacement DefaultPlacement => TuiPanelPlacement.Bottom;

    /// <inheritdoc />
    public override int DefaultSize => 12;

    /// <inheritdoc />
    public override object? Build(PanelContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        return PanelText.Clip(
            PanelRows.DiffRows(PanelExtractors.ExtractRecentChanges(ctx.State, 8), ctx.Width),
            ctx.Width,
            ctx.Height);
    }
}
