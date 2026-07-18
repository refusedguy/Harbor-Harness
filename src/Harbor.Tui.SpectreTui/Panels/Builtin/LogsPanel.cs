using Harbor.Ui.Framework.Diagnostics;
using Harbor.Ui.Framework.Panels;
using Harbor.Ui.Framework.State;
using Harbor.Tui.SpectreTui.View;
using Microsoft.Extensions.Logging;
using Spectre.Console;
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
        var p = new Paragraph().Alignment(Justify.Left);
        p.Lines.Add(TextLine.FromMarkup(
            "[bold cyan]Logs[/] [grey](F12 to hide · live ILogger output · file at ~/.harbor/logs/)[/]"));
        p.Lines.Add(TextLine.FromMarkup("[grey]─────────────────────────────[/]"));

        var panel = ResolvePanel(ctx);
        if (panel is null)
        {
            p.Lines.Add(TextLine.FromMarkup(
                "[yellow]Diagnostics panel not registered.[/]"));
            p.Lines.Add(TextLine.FromMarkup(
                "[grey]This should never happen in production — HostBuilder registers[/]"));
            p.Lines.Add(TextLine.FromMarkup(
                "[grey]IDiagnosticsPanel whenever an interactive TUI is active.[/]"));
            return p;
        }

        int maxVisible = Math.Max(2, ctx.Height - 4);
        int requested = Math.Min(maxVisible, 50);
        var entries = panel.GetRecent(requested);
        if (entries.Count == 0)
        {
            p.Lines.Add(TextLine.FromMarkup("[green]No log entries yet.[/]"));
            p.Lines.Add(TextLine.FromMarkup(
                "[grey]Logs from every ILogger will appear here in arrival order.[/]"));
            return p;
        }

        foreach (var entry in entries)
        {
            string color = LevelColor(entry.Level);
            string levelTag = entry.Level switch
            {
                LogLevel.Trace => "TRAC",
                LogLevel.Debug => "DBUG",
                LogLevel.Information => "INFO",
                LogLevel.Warning => "WARN",
                LogLevel.Error => "ERRO",
                LogLevel.Critical => "CRIT",
                _ => "????"
            };
            string time = entry.Timestamp.ToLocalTime().ToString("HH:mm:ss.fff");
            string category = ShortenCategory(entry.Category);
            string body = ChatMarkup.Escape(Truncate(entry.Message, ctx.Width - time.Length - levelTag.Length - category.Length - 7));
            string bold = entry.Level == LogLevel.Critical ? "bold " : string.Empty;
            p.Lines.Add(TextLine.FromMarkup(
                $"[grey]{time}[/] [{bold}{color}]{levelTag}[/] [grey]{category}[/] {body}"));
        }

        p.Lines.Add(TextLine.FromMarkup("[grey]─────────────────────────────[/]"));
        p.Lines.Add(TextLine.FromMarkup("[grey]F12 toggle · Ctrl+L clear console (does not clear this buffer)[/]"));
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
                store.Dispatch(new UiMsg.TogglePanel(Id));
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

    private static string LevelColor(LogLevel level) => level switch
    {
        LogLevel.Trace => "grey",
        LogLevel.Debug => "grey",
        LogLevel.Information => "white",
        LogLevel.Warning => "yellow",
        LogLevel.Error => "red",
        LogLevel.Critical => "red",
        _ => "white"
    };

    private static string ShortenCategory(string category)
    {
        if (string.IsNullOrEmpty(category))
            return "-";
        // Show last segment of "Harbor.Core.Sessions.AgentLoop" → "AgentLoop".
        int lastDot = category.LastIndexOf('.');
        return lastDot >= 0 && lastDot < category.Length - 1
            ? category[(lastDot + 1)..]
            : category;
    }

    private static string Truncate(string text, int max)
    {
        if (max <= 3) return text;
        if (string.IsNullOrEmpty(text)) return string.Empty;
        // Collapse newlines so a multi-line log entry stays on one display row.
        string single = text.Replace("\r", " ").Replace("\n", " ");
        return single.Length <= max ? single : single[..(max - 1)] + "…";
    }
}
