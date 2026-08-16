using System.Text;
using Harbor.Tui.RazorConsole.Rendering;
using Harbor.Ui.Framework.State;
namespace Harbor.Tui.RazorConsole.Views;
/// <summary>
///     Ctrl+P command palette: fuzzy-search over slash commands + registered
///     panels + recent sessions. Pure projection — selection state lives in
///     <see cref="CommandPaletteState" /> (held by the caller).
/// </summary>
public sealed class CommandPaletteView
{
    /// <summary>Render the palette popup. <paramref name="query" /> filters the items.</summary>
    public string Build(string query, IReadOnlyList<string> panels, IReadOnlyList<string> sessions)
    {
        var sb = new StringBuilder(256);
        sb.Append("[cyan]┌─ command palette ─────────────┐[/]\n");
        sb.Append($"[grey]│ [/][yellow]{RazorMarkdownRenderer.Escape(query)}[/][grey]▍[/]\n");

        int shown = 0;
        foreach (string cmd in ChatCommands.Slash)
        {
            if (!string.IsNullOrEmpty(query) && !cmd.Contains(query, StringComparison.OrdinalIgnoreCase))
                continue;
            sb.Append($"[grey]│ [/][white]{cmd}[/]\n");
            if (++shown >= 8) break;
        }

        foreach (string p in panels)
        {
            if (!string.IsNullOrEmpty(query) && !p.Contains(query, StringComparison.OrdinalIgnoreCase))
                continue;
            sb.Append($"[grey]│ [/][blue]panel: {RazorMarkdownRenderer.Escape(p)}[/]\n");
            if (++shown >= 12) break;
        }

        foreach (string sess in sessions)
        {
            if (!string.IsNullOrEmpty(query) && !sess.Contains(query, StringComparison.OrdinalIgnoreCase))
                continue;
            sb.Append($"[grey]│ [/][magenta]session: {RazorMarkdownRenderer.Escape(sess)}[/]\n");
            if (++shown >= 16) break;
        }

        sb.Append("[grey]└──────────────────────────────┘[/]\n");
        return sb.ToString();
    }
}

/// <summary>Mutable palette state held by the bridge (query + open flag).</summary>
public sealed class CommandPaletteState
{
    public bool IsOpen { get; set; }
    public string Query { get; set; } = string.Empty;
}
