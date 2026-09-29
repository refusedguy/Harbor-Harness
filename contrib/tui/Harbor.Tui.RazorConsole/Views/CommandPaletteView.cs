using System.Text;
using Harbor.Tui.RazorConsole.Rendering;
using Harbor.Ui.Framework.Projection;
namespace Harbor.Tui.RazorConsole.Views;
/// <summary>
///     Ctrl+P command palette: fuzzy-search over slash commands + registered
///     panels + recent sessions. Pure projection — selection state lives in
///     <see cref="CommandPaletteState" /> (held by the caller).
/// </summary>
/// <remarks>
///     Row selection and untrusted-text scrubbing are shared with the Termina and
///     TerminalGui shells through <see cref="PaletteRows" />; this class only adds
///     the RazorConsole-specific half — Spectre markup: every value is passed
///     through <see cref="RazorMarkdownRenderer.Escape" /> so a title reading
///     <c>[/]</c> cannot close the colour tag it sits in (issue #554).
/// </remarks>
public sealed class CommandPaletteView
{
    /// <summary>Render the palette popup. <paramref name="query" /> filters the items.</summary>
    public string Build(string query, IReadOnlyList<string> panels, IReadOnlyList<string> sessions)
    {
        var sb = new StringBuilder(256);
        sb.Append("[cyan]┌─ command palette ─────────────┐[/]\n");
        sb.Append($"[grey]│ [/][yellow]{RazorMarkdownRenderer.Escape(PaletteRows.Sanitize(query))}[/][grey]▍[/]\n");

        foreach (PaletteRow row in PaletteRows.Build(query, panels, sessions))
        {
            var (markup, text) = Style(row);
            sb.Append($"[grey]│ [/][{markup}]{RazorMarkdownRenderer.Escape(text)}[/]\n");
        }

        sb.Append("[grey]└──────────────────────────────┘[/]\n");
        return sb.ToString();
    }

    /// <summary>Row colour plus the prefix the row kind carries. Text is pre-sanitized.</summary>
    private static (string Markup, string Text) Style(PaletteRow row) => row.Kind switch
    {
        PaletteRowKind.Panel => ("blue", $"panel: {row.Text}"),
        PaletteRowKind.Session => ("magenta", $"session: {row.Text}"),
        _ => ("white", row.Text),
    };
}

/// <summary>Mutable palette state held by the bridge (query + open flag).</summary>
public sealed class CommandPaletteState
{
    public bool IsOpen { get; set; }
    public string Query { get; set; } = string.Empty;
}
