using System.Text;
using Harbor.Tui.TerminalGui.Handlers;
using Harbor.Ui.Framework.State;
namespace Harbor.Tui.TerminalGui.Views;
/// <summary>
///     Status bar projection: <c>provider/model/agent · tokens↑↓ · $cost · status · scroll%</c>.
///     Reads only from <see cref="UiState" /> — no local mutable counters.
/// </summary>
public sealed class StatusBarView
{
    /// <summary>Build the status bar text for the supplied state.</summary>
    public string Build(UiState s)
    {
        var sb = new StringBuilder(128);
        sb.Append(StatusGlyph(s)).Append(' ').Append(s.Status);
        sb.Append("  ·  ").Append(s.Provider).Append('/').Append(s.Model);
        sb.Append("  ·  agent ").Append(s.AgentName);
        sb.Append("  ·  ").Append(s.Cost.TokensIn).Append("↑ ");
        sb.Append(s.Cost.TokensOut).Append("↓ $").Append(s.Cost.CostUsd.ToString("F4"));
        sb.Append("  ·  ").Append(ScrollHandler.ScrollPercent(s));
        return sb.ToString();
    }

    private static string StatusGlyph(UiState s) => s.Status switch
    {
        "running" => "▌",
        "compacting" => "◐",
        "error" => "✗",
        _ => "○"
    };
}
