using System.Text;
using Harbor.Ui.Framework.State;
using Harbor.Tui.Termina.Handlers;
using Harbor.Tui.Termina.Rendering;
using TerminaColor = Termina.Terminal.Color;

namespace Harbor.Tui.Termina.Views;

/// <summary>
///     Status bar projection: <c>provider/model/agent · tokens↑↓ · $cost · status · scroll%</c>.
///     Mirrors SpectreTui's footer semantics; reads only from <see cref="UiState" />
///     (no local mutable counters).
/// </summary>
public sealed class StatusBarView
{
    /// <summary>Build the status bar text for the supplied state.</summary>
    public string Build(UiState s)
    {
        var sb = new StringBuilder(128);
        sb.Append(TerminaMarkdownRenderer.Ansi(TerminaColor.DarkGray, " "));
        sb.Append(TerminaMarkdownRenderer.Ansi(StatusColor(s), StatusGlyph(s)));
        sb.Append(' ').Append(TerminaMarkdownRenderer.Ansi(StatusColor(s), s.Status));
        sb.Append(TerminaMarkdownRenderer.Ansi(TerminaColor.DarkGray, "  ·  "));
        sb.Append(TerminaMarkdownRenderer.Ansi(TerminaColor.Cyan, s.Provider));
        sb.Append(TerminaMarkdownRenderer.Ansi(TerminaColor.DarkGray, "/"));
        sb.Append(TerminaMarkdownRenderer.Ansi(TerminaColor.White, s.Model));
        sb.Append(TerminaMarkdownRenderer.Ansi(TerminaColor.DarkGray, "  ·  agent "));
        sb.Append(TerminaMarkdownRenderer.Ansi(TerminaColor.Cyan, s.AgentName));
        sb.Append(TerminaMarkdownRenderer.Ansi(TerminaColor.DarkGray, "  ·  "));
        sb.Append(TerminaMarkdownRenderer.Ansi(TerminaColor.Green, $"{s.Cost.TokensIn}↑"));
        sb.Append(' ').Append(TerminaMarkdownRenderer.Ansi(TerminaColor.Magenta, $"{s.Cost.TokensOut}↓"));
        sb.Append(' ').Append(TerminaMarkdownRenderer.Ansi(TerminaColor.Yellow, $"${s.Cost.CostUsd:F4}"));
        sb.Append(TerminaMarkdownRenderer.Ansi(TerminaColor.DarkGray, "  ·  "));
        sb.Append(TerminaMarkdownRenderer.Ansi(TerminaColor.DarkGray, ScrollHandler.ScrollPercent(s)));
        return sb.ToString();
    }

    private static TerminaColor StatusColor(UiState s) => s.Status switch
    {
        "running" => TerminaColor.Cyan,
        "compacting" => TerminaColor.Yellow,
        "error" => TerminaColor.Red,
        _ => TerminaColor.Gray
    };

    private static string StatusGlyph(UiState s) => s.Status switch
    {
        "running" => "▌",
        "compacting" => "◐",
        "error" => "✗",
        _ => "○"
    };
}
