using Harbor.Tui.SpectreTui.View;
using Harbor.Ui.Framework.Diagnostics;
using Harbor.Ui.Framework.Panels;
using Harbor.Ui.Framework.Projection;
using Harbor.Ui.Framework.State;
using Harbor.Abstractions.Models;
using Spectre.Tui;
namespace Harbor.Tui.SpectreTui.Panels.Builtin;
/// <summary>
///     Builtin panel that surfaces live <c>ILogger</c> output inside the
///     SpectreTUI interactive renderer. Toggled with <c>F12</c>.
/// </summary>
/// <remarks>
///     <para>
///         <b>Why this exists:</b> the SpectreTUI full-screen renderer owns the
///         alternate screen buffer; any <c>Console.WriteLine</c> from the
///         simple-console logger would corrupt the rendered frame. The CLI's
///         <c>HostBuilder</c> therefore detaches the console logger when an
///         interactive TUI is active and routes log entries into the shared
///         <see cref="InMemoryDiagnosticsPanel" /> singleton instead. This panel
///         is the user-facing surface for that buffer.
///     </para>
///     <para>
///         <b>Source:</b> resolves <see cref="IDiagnosticsPanel" /> from
///         <see cref="PanelContext.Services" />. Falls back to an empty
///         placeholder when no panel is registered (e.g. unit tests).
///     </para>
///     <para>
///         <b>Rendering:</b> shows the last N entries (driven by available
///         height, max 50) with color by log level — Trace/Debug=grey,
///         Information=white, Warning=yellow, Error/Critical=red (Critical bold).
///         Auto-scrolls to the bottom on every frame so the most-recent entries
///         are always visible. F12 (handled by the host) toggles visibility.
///     </para>
///     <para>
///         <b>Thread safety:</b> <see cref="Build" /> is called from the render
///         thread; <see cref="IDiagnosticsPanel" /> is internally synchronized.
///         No mutable state lives in this provider.
///     </para>
/// </remarks>
public sealed class LogsPanel : IPanelProvider
{
    /// <inheritdoc />
    public string Id => "logs";

    /// <inheritdoc />
    public string Title => "Logs";

    /// <inheritdoc />
    public TuiPanelPlacement DefaultPlacement => TuiPanelPlacement.Bottom;

    /// <inheritdoc />
    public int DefaultSize => 10;

    /// <inheritdoc />
    public object? Build(PanelContext ctx)
    {
        var panel = ResolvePanel(ctx);
        if (panel is null)
        {
            var p0 = new Paragraph().Alignment(Justify.Left);
            p0.Lines.Add(TextLine.FromMarkup(
                "[bold cyan]Logs[/] [grey](F12 to hide · live ILogger output · file at ~/.harbor/logs/)[/]"));
            p0.Lines.Add(TextLine.FromMarkup("[grey]" + PanelText.Separator + "[/]"));
            p0.Lines.Add(TextLine.FromMarkup(
                "[yellow]Diagnostics panel not registered.[/]"));
            p0.Lines.Add(TextLine.FromMarkup(
                "[grey]This should never happen in production — HostBuilder registers[/]"));
            p0.Lines.Add(TextLine.FromMarkup(
                "[grey]IDiagnosticsPanel whenever an interactive TUI is active.[/]"));
            return p0;
        }

        int maxVisible = Math.Max(2, ctx.Height - 4);
        int requested = Math.Min(maxVisible, 50);
        var rows = PanelRows.LogRows(panel.GetRecent(requested), ctx.Width, ctx.Height);

        var p = new Paragraph().Alignment(Justify.Left);
        foreach (string row in rows)
            p.Lines.Add(TextLine.FromMarkup(StyleRow(row)));
        return p;
    }

    /// <inheritdoc />
    public bool OnKey(UiKey key, PanelContext ctx)
    {
        // F12 while focused → toggle back off. Esc / 'q' are handled by the host
        // (ClosePanel) before the key reaches us.
        if (key.Code == UiKeyCode.F12)
        {
            if (ctx.Services?.GetService(typeof(UiStore)) is UiStore store)
                store.Dispatch(new AppMsg.TogglePanel(Id));
            return true;
        }
        return false;
    }

    private static IDiagnosticsPanel? ResolvePanel(PanelContext ctx)
    {
        if (ctx.Services is null)
            return null;
        return ctx.Services.GetService(typeof(IDiagnosticsPanel)) as IDiagnosticsPanel;
    }

    private static string StyleRow(string row)
    {
        if (row.StartsWith("Logs (", StringComparison.Ordinal))
        {
            int paren = row.IndexOf('(');
            string tail = paren >= 0 ? row[paren..] : string.Empty;
            return "[bold cyan]Logs[/] [grey]" + ChatMarkup.Escape(tail) + "[/]";
        }

        if (row == PanelText.Separator)
            return "[grey]" + PanelText.Separator + "[/]";

        if (row is "No log entries yet.")
            return "[green]" + ChatMarkup.Escape(row) + "[/]";

        if (row is "Logs from every ILogger will appear here in arrival order."
            or "F12 toggle · Ctrl+L clear console (does not clear this buffer)")
            return "[grey]" + ChatMarkup.Escape(row) + "[/]";

        // Entry row: "HH:mm:ss.fff LEVEL category body".
        if (row.Length > 18)
        {
            string levelTag = row.Length >= 17 ? row[13..17] : string.Empty;
            (string color, bool isBold) = levelTag switch
            {
                "TRAC" => ("grey", false),
                "DBUG" => ("grey", false),
                "INFO" => ("white", false),
                "WARN" => ("yellow", false),
                "ERRO" => ("red", false),
                "CRIT" => ("red", true),
                _ => ("white", false),
            };
            if (levelTag is "TRAC" or "DBUG" or "INFO" or "WARN" or "ERRO" or "CRIT" or "????")
            {
                string time = row[..12];
                string rest = row.Length > 18 ? row[18..] : string.Empty;
                int space = rest.IndexOf(' ');
                string category = space < 0 ? rest : rest[..space];
                string body = space < 0 ? string.Empty : rest[(space + 1)..];
                string boldPrefix = isBold ? "bold " : string.Empty;
                return ($"[grey]{ChatMarkup.Escape(time)}[/] [{boldPrefix}{color}]{levelTag}[/] " +
                    $"[grey]{ChatMarkup.Escape(category)}[/] {ChatMarkup.Escape(body)}").TrimEnd();
            }
        }

        return ChatMarkup.Escape(row);
    }
}
