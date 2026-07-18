using System.Text;
using Harbor.Ui.Framework.State;
using Harbor.Tui.RazorConsole.Handlers;
using Harbor.Tui.RazorConsole.Rendering;

namespace Harbor.Tui.RazorConsole.Views;

/// <summary>
///     Status bar projection: <c>provider/model/agent · tokens↑↓ · $cost · status · scroll%</c>.
/// </summary>
public sealed class StatusBarView
{
    /// <summary>Build the status bar markup for the supplied state.</summary>
    public string Build(UiState s)
    {
        var sb = new StringBuilder(160);
        sb.Append($"[{StatusMarkup(s)}]{StatusGlyph(s)} {s.Status}[/]");
        sb.Append(" [grey]·[/] ");
        sb.Append($"[cyan]{RazorMarkdownRenderer.Escape(s.Provider)}[/]");
        sb.Append("[grey]/[/]");
        sb.Append($"[white]{RazorMarkdownRenderer.Escape(s.Model)}[/]");
        sb.Append(" [grey]· agent[/] ");
        sb.Append($"[aqua]{RazorMarkdownRenderer.Escape(s.AgentName)}[/]");
        sb.Append(" [grey]·[/] ");
        sb.Append($"[green]{s.Cost.TokensIn}↑[/]");
        sb.Append(' ');
        sb.Append($"[magenta]{s.Cost.TokensOut}↓[/]");
        sb.Append(' ');
        sb.Append($"[yellow]${s.Cost.CostUsd:F4}[/]");
        sb.Append(" [grey]·[/] ");
        sb.Append($"[grey]{ScrollHandler.ScrollPercent(s)}[/]");
        return sb.ToString();
    }

    private static string StatusMarkup(UiState s) => s.Status switch
    {
        "running" => "cyan",
        "compacting" => "yellow",
        "error" => "red",
        _ => "grey"
    };

    private static string StatusGlyph(UiState s) => s.Status switch
    {
        "running" => "▌",
        "compacting" => "◐",
        "error" => "✗",
        _ => "○"
    };
}
