using System.Text;
using Harbor.Tui.RazorConsole.Rendering;
using Harbor.Ui.Framework.Panels;
using Harbor.Ui.Framework.State;
namespace Harbor.Tui.RazorConsole.Views;
/// <summary>
///     Session sidebar listing registered panels + a search filter and
///     new/branch/delete affordances. Mirrors SpectreTui's shape.
/// </summary>
public sealed class SessionSidebarView
{
    /// <summary>Render the sidebar body for the supplied state + registry.</summary>
    public string Build(UiState s, PanelRegistry registry, string? filter = null)
    {
        var sb = new StringBuilder(256);
        sb.Append("[cyan]─ sessions ─[/]\n");

        var providers = registry.All;
        if (providers.Count == 0)
        {
            sb.Append("[grey]  (no panels registered)[/]\n");
            return sb.ToString();
        }

        int slot = 1;
        foreach (string id in providers.Select(p => p.Id))
        {
            if (filter is not null && !id.Contains(filter, StringComparison.OrdinalIgnoreCase))
                continue;

            var state = s.PanelStates.TryGetValue(id, out var st) ? st : TuiPanelState.Hidden;
            bool focused = s.FocusedPanelId == id;
            string glyph = state == TuiPanelState.Hidden ? " " : focused ? "▸" : "·";
            string color = state == TuiPanelState.Hidden ? "grey"
                : focused ? "yellow" : "white";
            sb.Append($"[grey]  Alt+{slot} [/][{color}]{glyph} {RazorMarkdownRenderer.Escape(id)}[/]\n");
            slot++;
        }

        sb.Append("[grey]\n  /sessions  /new  /branch  /delete[/]\n");
        return sb.ToString();
    }
}
