using System.Text;
using Harbor.Ui.Framework.State;
using Harbor.Tui.Termina.Rendering;
using TerminaColor = Termina.Terminal.Color;

namespace Harbor.Tui.Termina.Views;

/// <summary>
///     Ctrl+P command palette: fuzzy-search over slash commands + registered
///     panels + recent sessions. Pure projection — selection state lives in
///     <see cref="CommandPaletteState" /> (held by the caller). The renderer
///     only emits the visible rows.
/// </summary>
public sealed class CommandPaletteView
{
    /// <summary>Render the palette popup. <paramref name="query" /> filters the items.</summary>
    public string Build(string query, IReadOnlyList<string> panels, IReadOnlyList<string> sessions)
    {
        var sb = new StringBuilder(256);
        sb.Append(TerminaMarkdownRenderer.Ansi(TerminaColor.Cyan, "┌─ command palette ─────────────┐\n"));
        sb.Append(TerminaMarkdownRenderer.Ansi(TerminaColor.DarkGray, "│ "))
          .Append(TerminaMarkdownRenderer.Ansi(TerminaColor.Yellow, query))
          .Append(TerminaMarkdownRenderer.Ansi(TerminaColor.DarkGray, "▍\n"));

        int shown = 0;
        foreach (var cmd in ChatCommands.Slash)
        {
            if (!string.IsNullOrEmpty(query) && !cmd.Contains(query, StringComparison.OrdinalIgnoreCase))
                continue;
            sb.Append(TerminaMarkdownRenderer.Ansi(TerminaColor.DarkGray, "│ "))
              .Append(TerminaMarkdownRenderer.Ansi(TerminaColor.White, cmd)).Append('\n');
            if (++shown >= 8) break;
        }

        foreach (var p in panels)
        {
            if (!string.IsNullOrEmpty(query) && !p.Contains(query, StringComparison.OrdinalIgnoreCase))
                continue;
            sb.Append(TerminaMarkdownRenderer.Ansi(TerminaColor.DarkGray, "│ "))
              .Append(TerminaMarkdownRenderer.Ansi(TerminaColor.Blue, $"panel: {p}")).Append('\n');
            if (++shown >= 12) break;
        }

        foreach (var sess in sessions)
        {
            if (!string.IsNullOrEmpty(query) && !sess.Contains(query, StringComparison.OrdinalIgnoreCase))
                continue;
            sb.Append(TerminaMarkdownRenderer.Ansi(TerminaColor.DarkGray, "│ "))
              .Append(TerminaMarkdownRenderer.Ansi(TerminaColor.Magenta, $"session: {sess}")).Append('\n');
            if (++shown >= 16) break;
        }

        sb.Append(TerminaMarkdownRenderer.Ansi(TerminaColor.DarkGray, "└──────────────────────────────┘\n"));
        return sb.ToString();
    }
}

/// <summary>Mutable palette state held by the bridge (query + open flag).</summary>
public sealed class CommandPaletteState
{
    public bool IsOpen { get; set; }
    public string Query { get; set; } = string.Empty;
}
