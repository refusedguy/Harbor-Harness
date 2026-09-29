using System.Text;
using Harbor.Ui.Framework.Projection;
namespace Harbor.Tui.TerminalGui.Views;
/// <summary>
///     Ctrl+P command palette: fuzzy-search over slash commands + registered
///     panels + recent sessions. Pure projection — selection state lives in
///     <see cref="CommandPaletteState" /> (held by the caller).
/// </summary>
/// <remarks>
///     Row selection and untrusted-text scrubbing are shared with the RazorConsole
///     and Termina shells through <see cref="PaletteRows" />, which drops ANSI
///     escape sequences and control characters from every value before it reaches
///     a terminal (issue #554). Terminal.Gui paints colour per-attribute rather
///     than with markup, so this shell has no dialect left to escape.
/// </remarks>
public sealed class CommandPaletteView
{
    /// <summary>Render the palette popup. <paramref name="query" /> filters the items.</summary>
    public string Build(string query, IReadOnlyList<string> panels, IReadOnlyList<string> sessions)
    {
        var sb = new StringBuilder(256);
        sb.Append("┌─ command palette ─────────────┐\n");
        sb.Append("│ ").Append(PaletteRows.Sanitize(query)).Append("▍\n");

        foreach (PaletteRow row in PaletteRows.Build(query, panels, sessions))
        {
            sb.Append("│ ").Append(Text(row)).Append('\n');
        }

        sb.Append("└──────────────────────────────┘\n");
        return sb.ToString();
    }

    /// <summary>Row prefix the row kind carries. Text is pre-sanitized.</summary>
    private static string Text(PaletteRow row) => row.Kind switch
    {
        PaletteRowKind.Panel => $"panel: {row.Text}",
        PaletteRowKind.Session => $"session: {row.Text}",
        _ => row.Text,
    };
}

/// <summary>Mutable palette state held by the bridge (query + open flag).</summary>
public sealed class CommandPaletteState
{
    public bool IsOpen { get; set; }
    public string Query { get; set; } = string.Empty;
}
