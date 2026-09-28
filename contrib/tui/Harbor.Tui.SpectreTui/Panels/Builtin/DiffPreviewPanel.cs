using Harbor.Tui.SpectreTui.View;
using Harbor.Ui.Framework.Panels;
using Harbor.Ui.Framework.Projection;
using Harbor.Ui.Framework.State;
using Harbor.Abstractions.Models;
using Spectre.Tui;
namespace Harbor.Tui.SpectreTui.Panels.Builtin;
/// <summary>
///     Builtin panel that shows recent file changes from <c>edit</c>, <c>write</c>,
///     and <c>read</c> tool calls. Parses tool args from the transcript and renders a
///     compact "Recent File Changes" view; clicking (when supported) jumps to the
///     file in the editor.
/// </summary>
/// <remarks>
///     <para>
///         <b>Decoupling:</b> like <see cref="TodoListPanel" />, this panel reads only
///         from <see cref="UiState.Chat.Lines" />. It does not call <c>git</c> or open files
///         itself — that's the responsibility of the agent via tool calls.
///     </para>
///     <para>
///         <b>Diff source:</b> when an <c>edit</c> tool run produces a result that
///         contains unified-diff hunk markers (<c>@@</c>, <c>+</c>, <c>-</c>), the
///         panel renders the diff inline. Otherwise it shows the result preview.
///     </para>
/// </remarks>
public sealed class DiffPreviewPanel : IPanelProvider
{
    /// <inheritdoc />
    public string Id => "diff-preview";

    /// <inheritdoc />
    public string Title => "Diff Preview";

    /// <inheritdoc />
    public TuiPanelPlacement DefaultPlacement => TuiPanelPlacement.Bottom;

    /// <inheritdoc />
    public int DefaultSize => 12;

    /// <inheritdoc />
    public object? Build(PanelContext ctx)
    {
        var changes = PanelExtractors.ExtractRecentChanges(ctx.State, 8);
        var rows = PanelRows.DiffRows(changes, ctx.Width);

        var p = new Paragraph().Alignment(Justify.Left);
        foreach (string row in rows)
            p.Lines.Add(TextLine.FromMarkup(StyleRow(row)));
        return p;
    }

    /// <inheritdoc />
    public bool OnKey(UiKey key, PanelContext ctx) => false;

    private static string StyleRow(string row)
    {
        if (row.StartsWith("Diff Preview (", StringComparison.Ordinal))
        {
            int paren = row.IndexOf('(');
            string tail = paren >= 0 ? row[paren..] : string.Empty;
            return "[bold cyan]Diff Preview[/] [grey]" + ChatMarkup.Escape(tail) + "[/]";
        }

        if (row == PanelText.Separator)
            return "[grey]" + PanelText.Separator + "[/]";

        if (row.StartsWith("No file edits yet.", StringComparison.Ordinal) ||
            row.StartsWith("Edits made by the agent", StringComparison.Ordinal))
            return "[grey]" + ChatMarkup.Escape(row) + "[/]";

        string trimmed = row.TrimStart();
        if (trimmed.Length >= 4 &&
            (trimmed[0] is '✎' or '✚' or '▸' or '⌥' or '·') &&
            (trimmed[2] is '✓' or '✗'))
        {
            string icon = trimmed[0] switch
            {
                '✎' => "[yellow]✎[/]",
                '✚' => "[green]✚[/]",
                '▸' => "[grey]▸[/]",
                '⌥' => "[blue]⌥[/]",
                _ => "[grey]·[/]",
            };
            string ok = trimmed[2] == '✗' ? "[red]✗[/]" : "[green]✓[/]";
            string path = trimmed.Length > 4 ? trimmed[4..] : string.Empty;
            return $"{icon} {ok} [bold]{ChatMarkup.Escape(path)}[/]";
        }

        // Diff body row ("  <line>"): color by the diff marker, escape first
        // to avoid markup injection from the diff body.
        string body = row.StartsWith("  ", StringComparison.Ordinal) ? row[2..] : row;
        if (body.Length == 0)
            return "[grey] [/]";
        string e = ChatMarkup.Escape(body);
        string color = body[0] switch
        {
            '+' => "green",
            '-' => "red",
            '@' => "cyan",
            _ => "grey",
        };
        return $"  [{color}]{e}[/]";
    }
}
