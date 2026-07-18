using System.Collections.Generic;
using System.Text.RegularExpressions;
using Harbor.Tui.Abstractions.Panels;
using Harbor.Tui.Abstractions.State;
using Harbor.Tui.SpectreTui.View;
using Spectre.Console;
using Spectre.Tui;
namespace Harbor.Tui.SpectreTui.Panels.Builtin;

/// <summary>
///     Builtin panel that shows the live todo list contributed by the (optional)
///     <c>TodoWritePlugin</c>. Parses the most recent <c>todo</c> tool result line
///     from <see cref="UiState.Lines" />, so the panel auto-refreshes on every
///     <c>ToolExecutionEndEvent</c> without depending on the plugin assembly.
/// </summary>
/// <remarks>
///     <para>
///         <b>Decoupling:</b> the panel never references <c>Harbor.Plugin.TodoWrite</c>
///         (which is a sample DLL plugin that may not be loaded). Instead it scans the
///         transcript for the <c>todo</c> tool's output and re-parses the
///         <c>[ ]/[~]/[x]</c> status markers the tool emits.
///     </para>
///     <para>
///         <b>Auto-refresh:</b> the host calls <see cref="Build" /> every frame the
///         panel is visible. The reducer already appends every
///         <c>ToolExecutionEndEvent</c> to <see cref="UiState.Lines" />, so the
///         panel picks up new state without its own event subscription.
///     </para>
/// </remarks>
public sealed class TodoListPanel : IPanelProvider
{
    private static readonly Regex TodoLineRegex = new(
        @"^\s*(\[[ xX~?]\])\s+(.*)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <inheritdoc />
    public string Id => "todo-list";

    /// <inheritdoc />
    public string Title => "Todo List";

    /// <inheritdoc />
    public TuiPanelPlacement DefaultPlacement => TuiPanelPlacement.Right;

    /// <inheritdoc />
    public int DefaultSize => 40;

    /// <inheritdoc />
    public object? Build(PanelContext ctx)
    {
        var todos = ExtractTodos(ctx.State);

        var p = new Paragraph().Alignment(Justify.Left);
        p.Lines.Add(TextLine.FromMarkup("[bold cyan]Todo List[/] " +
                                        $"[grey]({todos.Count} items)[/]"));
        p.Lines.Add(TextLine.FromMarkup("[grey]─────────────────────[/]"));

        if (todos.Count == 0)
        {
            p.Lines.Add(TextLine.FromMarkup("[grey]No todos yet.[/]"));
            p.Lines.Add(TextLine.FromMarkup("[grey]Ask the agent to use the[/] [bold]todo[/] [grey]tool.[/]"));
            return p;
        }

        int done = 0, inProgress = 0, pending = 0;
        foreach (var (marker, content) in todos)
        {
            string icon;
            string color;
            switch (marker)
            {
                case "[x]":
                case "[X]":
                    icon = "✓";
                    color = "green";
                    done++;
                    break;
                case "[~]":
                    icon = "→";
                    color = "yellow";
                    inProgress++;
                    break;
                case "[ ]":
                    icon = "○";
                    color = "grey";
                    pending++;
                    break;
                default:
                    icon = "?";
                    color = "red";
                    break;
            }
            p.Lines.Add(TextLine.FromMarkup(
                $"  [{color}]{icon}[/]  {ChatMarkup.Escape(Truncate(content, ctx.Width - 6))}"));
        }

        p.Lines.Add(TextLine.FromMarkup("[grey]─────────────────────[/]"));
        p.Lines.Add(TextLine.FromMarkup(
            $"[green]✓ {done}[/]  [yellow]→ {inProgress}[/]  [grey]○ {pending}[/]"));
        return p;
    }

    /// <inheritdoc />
    public bool OnKey(UiKey key, PanelContext ctx) => false;

    /// <summary>
    ///     Scan the transcript for the most recent <c>todo</c> tool output and parse
    ///     the lines into (marker, content) pairs. Returns an empty list when no
    ///     <c>todo</c> tool has been invoked yet.
    /// </summary>
    private static List<(string Marker, string Content)> ExtractTodos(UiState state)
    {
        var result = new List<(string, string)>(8);

        // Walk backwards from the latest line to find the most recent ToolResult line
        // that contains a todo-looking block ("[ ]"/"[~]"/"[x]" markers). Because the
        // reducer's FormatToolEnd stores the whole tool output in a single ChatLine.Text
        // (newlines preserved), we split by '\n' and inspect each sub-line.
        var lines = state.Lines;
        int blockStart = -1;
        for (int i = lines.Length - 1; i >= 0; i--)
        {
            var line = lines[i];
            if (line.Role is not ChatRole.ToolResult)
                continue;

            string text = line.Text ?? string.Empty;
            if (ContainsTodoMarker(text))
            {
                blockStart = i;
                break;
            }
        }

        if (blockStart < 0)
            return result;

        // Read forward from blockStart, splitting each ToolResult ChatLine by '\n' and
        // collecting lines that match the todo regex.
        for (int i = blockStart; i < lines.Length; i++)
        {
            var line = lines[i];
            string text = line.Text ?? string.Empty;

            foreach (var sub in text.Split('\n'))
            {
                string s = sub.TrimEnd('\r');
                var match = TodoLineRegex.Match(s);
                if (match.Success)
                {
                    result.Add((match.Groups[1].Value, match.Groups[2].Value.Trim()));
                }
            }

            // Stop at the next tool call — a new todo invocation would start a new block.
            if (i > blockStart && line.Role == ChatRole.Tool)
                break;
        }

        return result;
    }

    private static bool ContainsTodoMarker(string text)
    {
        // Quick scan: any of [ ], [~], [x], [X], [?] at the start of a sub-line.
        foreach (var sub in text.Split('\n'))
        {
            string s = sub.TrimStart();
            if (s.Length >= 3 && s[0] == '[' && s[2] == ']'
                && (s[1] == ' ' || s[1] == 'x' || s[1] == 'X' || s[1] == '~' || s[1] == '?'))
                return true;
        }
        return false;
    }

    private static string Truncate(string text, int max)
    {
        if (max <= 3) return text;
        return text.Length <= max ? text : text[..(max - 1)] + "…";
    }
}
