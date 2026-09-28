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
///     from <see cref="UiState.Chat.Lines" />, so the panel auto-refreshes on every
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
///         <c>ToolExecutionEndEvent</c> to <see cref="UiState.Chat.Lines" />, so the
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
        var rows = PanelRows.TodoRows(todos, ctx.Width);

        var p = new Paragraph().Alignment(Justify.Left);
        foreach (string row in rows)
            p.Lines.Add(TextLine.FromMarkup(StyleRow(row)));
        return p;
    }

    /// <inheritdoc />
    public bool OnKey(UiKey key, PanelContext ctx) => false;

    private static string StyleRow(string row)
    {
        if (row.StartsWith("Todo List (", StringComparison.Ordinal))
        {
            int paren = row.IndexOf('(');
            string tail = paren >= 0 ? row[paren..] : string.Empty;
            return "[bold cyan]Todo List[/] [grey]" + ChatMarkup.Escape(tail) + "[/]";
        }

        if (row == PanelText.Separator)
            return "[grey]" + PanelText.Separator + "[/]";

        string trimmed = row.TrimStart();
        if (row.StartsWith("  ", StringComparison.Ordinal) &&
            trimmed is ['✓', ..] or ['→', ..] or ['○', ..] or ['?', ..])
        {
            // Item row ("  <icon>  <content>"): color only the icon so
            // "?" inside the content is not styled.
            string icon = trimmed[0] switch
            {
                '✓' => "[green]✓[/]",
                '→' => "[yellow]→[/]",
                '○' => "[grey]○[/]",
                _ => "[red]?[/]",
            };
            string content = row.Length > 5 ? row[5..] : string.Empty;
            return $"  {icon}  {ChatMarkup.Escape(content)}";
        }

        if (trimmed.StartsWith("✓", StringComparison.Ordinal))
        {
            // Summary row ("✓ <d>  → <a>  ○ <p>"): numbers only, safe to colorize all icons.
            string e = ChatMarkup.Escape(row);
            e = e.Replace("✓", "[green]✓[/]", StringComparison.Ordinal)
                .Replace("→", "[yellow]→[/]", StringComparison.Ordinal)
                .Replace("○", "[grey]○[/]", StringComparison.Ordinal);
            return e;
        }

        if (row.StartsWith("No todos yet.", StringComparison.Ordinal) ||
            row.StartsWith("Ask the agent", StringComparison.Ordinal))
            return "[grey]" + ChatMarkup.Escape(row) + "[/]";

        return ChatMarkup.Escape(row);
    }
}
