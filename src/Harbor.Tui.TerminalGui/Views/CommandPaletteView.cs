using System.Text;
using Harbor.Ui.Framework.State;

namespace Harbor.Tui.TerminalGui.Views;

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
        sb.Append("┌─ command palette ─────────────┐\n");
        sb.Append("│ ").Append(query).Append("▍\n");

        int shown = 0;
        foreach (var cmd in ChatCommands.Slash)
        {
            if (!string.IsNullOrEmpty(query) && !cmd.Contains(query, StringComparison.OrdinalIgnoreCase))
                continue;
            sb.Append("│ ").Append(cmd).Append('\n');
            if (++shown >= 8) break;
        }

        foreach (var p in panels)
        {
            if (!string.IsNullOrEmpty(query) && !p.Contains(query, StringComparison.OrdinalIgnoreCase))
                continue;
            sb.Append("│ panel: ").Append(p).Append('\n');
            if (++shown >= 12) break;
        }

        foreach (var sess in sessions)
        {
            if (!string.IsNullOrEmpty(query) && !sess.Contains(query, StringComparison.OrdinalIgnoreCase))
                continue;
            sb.Append("│ session: ").Append(sess).Append('\n');
            if (++shown >= 16) break;
        }

        sb.Append("└──────────────────────────────┘\n");
        return sb.ToString();
    }
}

/// <summary>Mutable palette state held by the bridge (query + open flag).</summary>
public sealed class CommandPaletteState
{
    public bool IsOpen { get; set; }
    public string Query { get; set; } = string.Empty;
}
