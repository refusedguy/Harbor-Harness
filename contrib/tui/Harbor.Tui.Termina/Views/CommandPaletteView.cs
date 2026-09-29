using System.Text;
using Harbor.Tui.Termina.Rendering;
using Harbor.Ui.Framework.Projection;
using TerminaColor = Termina.Terminal.Color;

namespace Harbor.Tui.Termina.Views;
/// <summary>
///     Ctrl+P command palette: fuzzy-search over slash commands + registered
///     panels + recent sessions. Pure projection — selection state lives in
///     <see cref="CommandPaletteState" /> (held by the caller). The renderer
///     only emits the visible rows.
/// </summary>
/// <remarks>
///     Row selection and untrusted-text scrubbing are shared with the RazorConsole
///     and TerminalGui shells through <see cref="PaletteRows" />, which drops ANSI
///     escape sequences and control characters from every value before it reaches
///     a terminal (issue #554). This class only adds the Termina-specific half —
///     24-bit colour around text that is already inert.
/// </remarks>
public sealed class CommandPaletteView
{
    /// <summary>Render the palette popup. <paramref name="query" /> filters the items.</summary>
    public string Build(string query, IReadOnlyList<string> panels, IReadOnlyList<string> sessions)
    {
        var sb = new StringBuilder(256);
        sb.Append(TerminaMarkdownRenderer.Ansi(TerminaColor.Cyan, "┌─ command palette ─────────────┐\n"));
        sb.Append(TerminaMarkdownRenderer.Ansi(TerminaColor.DarkGray, "│ "))
            .Append(TerminaMarkdownRenderer.Ansi(TerminaColor.Yellow, PaletteRows.Sanitize(query)))
            .Append(TerminaMarkdownRenderer.Ansi(TerminaColor.DarkGray, "▍\n"));

        foreach (PaletteRow row in PaletteRows.Build(query, panels, sessions))
        {
            var (color, text) = Style(row);
            sb.Append(TerminaMarkdownRenderer.Ansi(TerminaColor.DarkGray, "│ "))
                .Append(TerminaMarkdownRenderer.Ansi(color, text)).Append('\n');
        }

        sb.Append(TerminaMarkdownRenderer.Ansi(TerminaColor.DarkGray, "└──────────────────────────────┘\n"));
        return sb.ToString();
    }

    /// <summary>Row colour plus the prefix the row kind carries. Text is pre-sanitized.</summary>
    private static (TerminaColor Color, string Text) Style(PaletteRow row) => row.Kind switch
    {
        PaletteRowKind.Panel => (TerminaColor.Blue, $"panel: {row.Text}"),
        PaletteRowKind.Session => (TerminaColor.Magenta, $"session: {row.Text}"),
        _ => (TerminaColor.White, row.Text),
    };
}

/// <summary>Mutable palette state held by the bridge (query + open flag).</summary>
public sealed class CommandPaletteState
{
    public bool IsOpen { get; set; }
    public string Query { get; set; } = string.Empty;
}
