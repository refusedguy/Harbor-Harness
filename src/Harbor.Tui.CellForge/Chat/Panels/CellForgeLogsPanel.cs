using Harbor.Ui.Framework.Diagnostics;
using Harbor.Ui.Framework.Panels;
using Harbor.Ui.Framework.Projection;
using Harbor.Ui.Framework.State;

namespace Harbor.Tui.CellForge.Panels;

/// <summary>
///     Cell-native logs panel: live <c>ILogger</c> output surfaced from
///     <see cref="IDiagnosticsPanel"/> on <see cref="PanelServices"/>. <c>F12</c> while
///     focused dispatches <c>AppMsg.TogglePanel("logs")</c>.
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
        // #470: the diagnostics buffer is an explicitly typed field on
        // PanelServices, filled by the composition root — not a per-frame lookup
        // against a container the host may never have handed us.
        var panel = ctx.Deps.Diagnostics;
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
            // #470: the store is an explicit field on PanelServices — no lookup.
            if (ctx.Deps.Store is { } store)
            {
                _ = store.Dispatch(new AppMsg.TogglePanel(Id));
            }

            return true;
        }

        return false;
    }
}
