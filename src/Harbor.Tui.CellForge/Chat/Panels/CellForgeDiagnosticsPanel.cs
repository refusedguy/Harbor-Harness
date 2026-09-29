using Harbor.Ui.Framework.Panels;
using Harbor.Ui.Framework.Projection;
using Harbor.Ui.Framework.State;

namespace Harbor.Tui.CellForge.Panels;

/// <summary>
///     Cell-native diagnostics panel: transcript errors classified via
///     <see cref="PanelExtractors.CollectDiagnostics(UiState)"/>, one row per issue
///     (<c>✗ message</c> for errors, <c>▲ message</c> for warnings).
///     <c>j</c> / <c>k</c> move the cursor, which lives in
///     <see cref="UiState.Ui.PanelCursors"/> keyed by panel id (FP-005/TEA, #360);
///     scroll-to-source stays host-side.
/// </summary>
/// <remarks>
///     <c>Build</c> reads the cursor from <c>ctx.State</c> (missing key = 0) and
///     clamps it for display without persisting; <c>OnKey</c> folds the move
///     through <c>ctx.Deps.Store</c> via <c>AppMsg.SetPanelCursor</c>. The
///     provider-local fallback covers only the null-store degraded path
///     (tests); the lock keeps <c>Build</c> (render thread) and <c>OnKey</c>
///     (input thread) thread-safe.
/// </remarks>
public sealed class CellForgeDiagnosticsPanel : CellForgePanelBase
{
    private readonly object _gate = new();
    private int _fallbackCursor;

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
        int cursor = ResolveCursor(ctx);
        cursor = diagnostics.Count == 0 ? 0 : Math.Clamp(cursor, 0, diagnostics.Count - 1);

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
            {
                int max = PanelExtractors.CollectDiagnostics(ctx.State).Count - 1;
                int next;
                lock (_gate)
                {
                    next = max < 0 ? 0 : Math.Min(max, ResolveCursor(ctx) + 1);
                    _fallbackCursor = next;
                }

                if (ctx.Deps.Store is { } store)
                {
                    _ = store.Dispatch(new AppMsg.SetPanelCursor(Id, next));
                }

                return true;
            }

            case 'k':
            case 'K':
            {
                int next;
                lock (_gate)
                {
                    next = Math.Max(0, ResolveCursor(ctx) - 1);
                    _fallbackCursor = next;
                }

                if (ctx.Deps.Store is { } store)
                {
                    _ = store.Dispatch(new AppMsg.SetPanelCursor(Id, next));
                }

                return true;
            }

            default:
                return false;
        }
    }

    private int ResolveCursor(PanelContext ctx)
    {
        if (ctx.State.Ui.PanelCursors.TryGetValue(Id, out int stored))
        {
            return Math.Max(0, stored);
        }

        lock (_gate)
        {
            return _fallbackCursor;
        }
    }
}
