using System.Text;
using Harbor.Tui.Termina.Rendering;
using Harbor.Ui.Framework.Panels;
using Harbor.Ui.Framework.State;
using Harbor.Abstractions.Models;
using TerminaColor = Termina.Terminal.Color;

namespace Harbor.Tui.Termina.Views;
/// <summary>
///     Session sidebar listing registered panels + a search filter and
///     new/branch/delete affordances. Visibility for any given panel comes
///     from <see cref="UiState.PanelStates" /> (single source of truth); the
///     registry only contributes the provider list. Mirrors SpectreTui's
///     <c>SessionSidebarView</c> shape.
/// </summary>
public sealed class SessionSidebarView
{
    /// <summary>Render the sidebar body for the supplied state + registry.</summary>
    public string Build(UiState s, PanelRegistry registry, string? filter = null)
    {
        var sb = new StringBuilder(256);
        sb.Append(TerminaMarkdownRenderer.Ansi(TerminaColor.Cyan, "─ sessions ─\n"));

        var providers = registry.All;
        if (providers.Count == 0)
        {
            sb.Append(TerminaMarkdownRenderer.Ansi(TerminaColor.DarkGray, "  (no panels registered)\n"));
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
            var color = state == TuiPanelState.Hidden ? TerminaColor.DarkGray
                : focused ? TerminaColor.Yellow : TerminaColor.White;

            sb.Append(TerminaMarkdownRenderer.Ansi(TerminaColor.DarkGray, $"  Alt+{slot} "))
                .Append(TerminaMarkdownRenderer.Ansi(color, $"{glyph} {id}\n"));
            slot++;
        }

        sb.Append(TerminaMarkdownRenderer.Ansi(TerminaColor.DarkGray,
            "\n  /sessions  /new  /branch  /delete\n"));
        return sb.ToString();
    }
}
