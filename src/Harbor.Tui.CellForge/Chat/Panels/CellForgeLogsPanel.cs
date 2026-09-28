using Harbor.Ui.Framework.Diagnostics;
using Harbor.Ui.Framework.Panels;
using Harbor.Ui.Framework.Projection;
using Harbor.Ui.Framework.State;
using Microsoft.Extensions.DependencyInjection;

namespace Harbor.Tui.CellForge.Panels;

/// <summary>
///     Cell-native logs panel: live <c>ILogger</c> output surfaced from
///     <see cref="IDiagnosticsPanel"/> in <c>ctx.Services</c>. <c>F12</c> while
///     focused dispatches <c>UiMsg.TogglePanel("logs")</c>.
/// </summary>
public sealed class CellForgeLogsPanel : CellForgePanelBase
{
    /// <inheritdoc />
    public override string Id => "logs";

    /// <inheritdoc />
    public override string Title => "Logs";

    /// <inheritdoc />
    public override TuiPanelPlacement DefaultPlacement => TuiPanelPlacement.Bottom;

    /// <inheritdoc />
    public override int DefaultSize => 10;

    /// <inheritdoc />
    public override object? Build(PanelContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        // #63 legitimate: same as above — diagnostics panel is host-registered,
        // unreachable via ctor on a framework-created panel instance.
        var panel = ctx.Services?.GetService<IDiagnosticsPanel>();
        if (panel is null)
        {
            var rows = new List<string>(16);
            rows.Add("Logs (F12 to hide · live ILogger output · file at ~/.harbor/logs/)");
            rows.Add(PanelText.Separator);
            rows.Add("Diagnostics panel not registered.");
            rows.Add("HostBuilder registers IDiagnosticsPanel for interactive TUIs.");
            return PanelText.Clip(rows, ctx.Width, ctx.Height);
        }

        int maxVisible = Math.Max(2, ctx.Height - 4);
        int requested = Math.Min(maxVisible, 50);
        return PanelText.Clip(
            PanelRows.LogRows(panel.GetRecent(requested), ctx.Width, ctx.Height),
            ctx.Width,
            ctx.Height);
    }

    /// <inheritdoc />
    public override bool OnKey(UiKey key, PanelContext ctx)
    {
        if (key.Code == UiKeyCode.F12)
        {
            // #63: explicit store from the host (no Services lookup).
            if (ctx.Store is UiStore store)
            {
                _ = store.Dispatch(new UiMsg.TogglePanel(Id));
            }

            return true;
        }

        return false;
    }
}
