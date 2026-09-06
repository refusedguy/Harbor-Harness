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
///         from <see cref="UiState.Lines" />. It does not call <c>git</c> or open files
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

        var p = new Paragraph().Alignment(Justify.Left);
        p.Lines.Add(TextLine.FromMarkup("[bold cyan]Diff Preview[/] " +
                                        $"[grey]({changes.Count} recent change(s))[/]"));
        p.Lines.Add(TextLine.FromMarkup("[grey]─────────────────────────────[/]"));

        if (changes.Count == 0)
        {
            p.Lines.Add(TextLine.FromMarkup("[grey]No file edits yet.[/]"));
            p.Lines.Add(TextLine.FromMarkup("[grey]Edits made by the agent will appear here.[/]"));
            return p;
        }

        foreach (var change in changes)
        {
            string icon = change.ToolName switch
            {
                "edit" => "[yellow]✎[/]",
                "write" => "[green]✚[/]",
                "read" => "[grey]▸[/]",
                "patch" => "[blue]⌥[/]",
                _ => "[grey]·[/]"
            };
            string okIcon = change.IsError ? "[red]✗[/]" : "[green]✓[/]";
            string path = ShortenPath(change.FilePath, ctx.Width - 12);
            p.Lines.Add(TextLine.FromMarkup(
                $"  {icon} {okIcon} [bold]{ChatMarkup.Escape(path)}[/]"));

            if (!string.IsNullOrEmpty(change.DiffBody))
            {
                foreach (string diffLine in SplitLines(change.DiffBody, 4))
                {
                    string rendered = RenderDiffLine(diffLine);
                    p.Lines.Add(TextLine.FromMarkup($"    {rendered}"));
                }
            }
        }
        return p;
    }

    /// <inheritdoc />
    public bool OnKey(UiKey key, PanelContext ctx) => false;

    private static string ShortenPath(string path, int max)
    {
        if (string.IsNullOrEmpty(path) || path.Length <= max)
            return path;
        // Keep the file name + a hint of the directory.
        int slash = path.LastIndexOfAny(['/', '\\']);
        if (slash < 0 || path.Length - slash > max - 3)
            return path[^(max - 1)] + "…" + path[^1];
        string file = path[slash..];
        string dir = path[..slash];
        if (dir.Length > max - file.Length - 3)
            dir = "…" + dir[^(max - file.Length - 4)..];
        return dir + file;
    }

    private static IEnumerable<string> SplitLines(string text, int maxLines)
    {
        if (string.IsNullOrEmpty(text))
            yield break;
        int count = 0;
        int start = 0;
        while (start < text.Length && count < maxLines)
        {
            int nl = text.IndexOf('\n', start);
            if (nl < 0)
            {
                yield return text[start..];
                yield break;
            }
            yield return text[start..nl];
            start = nl + 1;
            count++;
        }
    }

    private static string RenderDiffLine(string line)
    {
        if (string.IsNullOrEmpty(line))
            return "[grey] [/]";
        // Escape first to avoid markup injection from the diff body.
        string e = ChatMarkup.Escape(line);
        return line[0] switch
        {
            '+' => $"[green]{e}[/]",
            '-' => $"[red]{e}[/]",
            '@' => $"[cyan]{e}[/]",
            _ => $"[grey]{e}[/]"
        };
    }
}
