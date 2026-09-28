using Harbor.Tui.SpectreTui.View;
using Harbor.Ui.Framework.Panels;
using Harbor.Ui.Framework.Projection;
using Harbor.Ui.Framework.State;
using Harbor.Abstractions.Models;
using Spectre.Tui;
namespace Harbor.Tui.SpectreTui.Panels.Builtin;
/// <summary>
///     Builtin panel that shows a horizontal bar chart of token usage per turn —
///     input, output, reasoning, cache-read, cache-write. Numbers come straight
///     from <see cref="UiState.Cost" /> (cumulative) and the most recent
///     <c>StepFinishEvent</c> usage if present in the transcript.
/// </summary>
public sealed class TokenBreakdownPanel : IPanelProvider
{
    /// <inheritdoc />
    public string Id => "token-breakdown";

    /// <inheritdoc />
    public string Title => "Token Breakdown";

    /// <inheritdoc />
    public TuiPanelPlacement DefaultPlacement => TuiPanelPlacement.Bottom;

    /// <inheritdoc />
    public int DefaultSize => 10;

    /// <inheritdoc />
    public object? Build(PanelContext ctx)
    {
        var rows = PanelRows.TokenRows(
            ctx.State.Cost.TokensIn, ctx.State.Cost.TokensOut, ctx.State.Cost.CostUsd, ctx.Width);

        var p = new Paragraph().Alignment(Justify.Left);
        foreach (string row in rows)
            p.Lines.Add(TextLine.FromMarkup(StyleRow(row)));
        return p;
    }

    /// <inheritdoc />
    public bool OnKey(UiKey key, PanelContext ctx) => false;

    private static string StyleRow(string row)
    {
        if (row == "Token Breakdown")
            return "[bold cyan]Token Breakdown[/]";
        if (row == PanelText.Separator)
            return "[grey]" + PanelText.Separator + "[/]";
        if (row.StartsWith("in ", StringComparison.Ordinal))
            return "[green]in[/]" + ChatMarkup.Escape(row[2..]);
        if (row.StartsWith("out", StringComparison.Ordinal))
            return "[yellow]out[/]" + ChatMarkup.Escape(row[3..]);
        if (row.StartsWith("total", StringComparison.Ordinal))
            return "[bold]total[/]" + ChatMarkup.Escape(row[5..]);
        if (row.StartsWith("(cumulative", StringComparison.Ordinal))
            return "[grey]" + ChatMarkup.Escape(row) + "[/]";
        return ChatMarkup.Escape(row);
    }
}
