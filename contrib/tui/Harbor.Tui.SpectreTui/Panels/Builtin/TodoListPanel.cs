using Harbor.Tui.SpectreTui.View;
using Harbor.Ui.Framework.Panels;
using Harbor.Ui.Framework.Projection;
using Harbor.Ui.Framework.State;
using Harbor.Abstractions.Models;
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
        var todos = PanelExtractors.ExtractTodos(ctx.State);

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
        foreach ((string marker, string content) in todos)
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

    private static string Truncate(string text, int max)
    {
        if (max <= 3) return text;
        return text.Length <= max ? text : text[..(max - 1)] + "…";
    }
}
