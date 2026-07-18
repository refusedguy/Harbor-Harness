using System.Collections.Generic;
using System.Text.Json;
using Harbor.Ui.Framework.Panels;
using Harbor.Ui.Framework.State;
using Harbor.Tui.SpectreTui.View;
using Spectre.Console;
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
    private static readonly HashSet<string> TrackedTools =
        new(System.StringComparer.Ordinal) { "edit", "write", "read", "patch" };

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
        var changes = ExtractRecentChanges(ctx.State, max: 8);

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
                foreach (var diffLine in SplitLines(change.DiffBody, maxLines: 4))
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

    private static List<FileChange> ExtractRecentChanges(UiState state, int max)
    {
        var result = new List<FileChange>(max);
        var lines = state.Lines;

        // Walk forward; collect (Tool, ToolResult) pairs by call id. The reducer does not
        // store the call id in the rendered text, so we pair by adjacency: a Tool line
        // followed by a ToolResult line is the same call.
        for (int i = 0; i < lines.Length && result.Count < max; i++)
        {
            var line = lines[i];
            if (line.Role != ChatRole.Tool)
                continue;

            string text = line.Text ?? string.Empty;
            // FormatToolStart emits "→ <toolname>  <args json>" or "→ <toolname>".
            if (text.Length < 2 || text[0] != '→')
                continue;

            int spaceIdx = text.IndexOf(' ', 2);
            string toolName = spaceIdx < 0 ? text[2..] : text[2..spaceIdx];
            if (!TrackedTools.Contains(toolName))
                continue;

            string argsJson = spaceIdx < 0 ? "{}" : text[(spaceIdx + 1)..].TrimStart();
            string filePath = ExtractFilePath(argsJson);

            // Find the next ToolResult line — that's this call's result.
            string? resultText = null;
            bool isError = false;
            for (int j = i + 1; j < lines.Length; j++)
            {
                if (lines[j].Role == ChatRole.ToolResult)
                {
                    string rt = lines[j].Text ?? string.Empty;
                    if (rt.Length > 0 && rt[0] == '✗') isError = true;
                    if (rt.Length >= 2 && (rt[0] == '✓' || rt[0] == '✗') && rt[1] == ' ')
                        rt = rt[2..];
                    resultText = rt;
                    break;
                }
                if (lines[j].Role == ChatRole.Tool)
                    break; // next call started, no result captured
            }

            result.Add(new FileChange(toolName, filePath, resultText ?? string.Empty, isError));
        }

        // Most-recent-first order.
        result.Reverse();
        return result;
    }

    private static string ExtractFilePath(string argsJson)
    {
        if (string.IsNullOrWhiteSpace(argsJson))
            return "<unknown>";

        try
        {
            using var doc = JsonDocument.Parse(argsJson);
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (prop.Value.ValueKind == JsonValueKind.String &&
                    (prop.Name.Contains("file", System.StringComparison.OrdinalIgnoreCase)
                     || prop.Name.Contains("path", System.StringComparison.OrdinalIgnoreCase)))
                {
                    return prop.Value.GetString() ?? "<unknown>";
                }
            }
        }
        catch (JsonException)
        {
            // fall through
        }
        return "<unknown>";
    }

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

    private sealed record FileChange(
        string ToolName,
        string FilePath,
        string DiffBody,
        bool IsError);
}
