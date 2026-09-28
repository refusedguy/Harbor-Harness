using Harbor.Ui.Framework.Panels;
using Harbor.Ui.Framework.Projection;
using Harbor.Ui.Framework.State;

namespace Harbor.Tui.CellForge.Panels;

/// <summary>
///     Cell-native diagnostics panel: transcript errors classified via
///     <see cref="PanelExtractors.CollectDiagnostics(UiState)"/>, one row per issue
///     (<c>✗ message</c> for errors, <c>▲ message</c> for warnings).
///     <c>j</c> / <c>k</c> move the cursor (same provider-local compromise as
///     <see cref="CellForgeFileTreePanel"/>); scroll-to-source stays host-side.
/// </summary>
/// <remarks>
///     TODO(principles)[FP-005, TEA]: cursor is provider-local mutable state
///     instead of living in <see cref="UiState"/> keyed by panel id. Guarded by
///     a small lock so <c>Build</c> (render thread) and <c>OnKey</c> (input
///     thread) stay thread-safe; moving the cursor into the store is follow-up work.
///     Tracked in #360.
/// </remarks>
public sealed class CellForgeDiagnosticsPanel : CellForgePanelBase
{
    private readonly object _gate = new();
    private int _cursor;

    /// <inheritdoc />
    public override string Id => "diagnostics";

    /// <inheritdoc />
    public override string Title => "Diagnostics";

    /// <inheritdoc />
    public override TuiPanelPlacement DefaultPlacement => TuiPanelPlacement.Bottom;

    /// <inheritdoc />
    public override int DefaultSize => 10;

    /// <inheritdoc />
    public override object? Build(PanelContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        var diagnostics = PanelExtractors.CollectDiagnostics(ctx.State);
        int cursor;
        lock (_gate)
        {
            _cursor = diagnostics.Count == 0 ? 0 : Math.Clamp(_cursor, 0, diagnostics.Count - 1);
            cursor = _cursor;
        }

        return PanelText.Clip(
            PanelRows.DiagnosticsRows(diagnostics, cursor, ctx.Width, ctx.Height),
            ctx.Width,
            ctx.Height);
    }

    /// <inheritdoc />
    public override bool OnKey(UiKey key, PanelContext ctx)
    {
        if (key.Code != UiKeyCode.Char || key.Character is null)
        {
            return false;
        }

        switch (key.Character)
        {
            case 'j':
            case 'J':
                lock (_gate)
                {
                    int max = PanelExtractors.CollectDiagnostics(ctx.State).Count - 1;
                    _cursor = max < 0 ? 0 : Math.Min(max, _cursor + 1);
                }

                return true;
            case 'k':
            case 'K':
                lock (_gate)
                {
                    _cursor = Math.Max(0, _cursor - 1);
                }

                return true;
            default:
                return false;
        }
    }
}
