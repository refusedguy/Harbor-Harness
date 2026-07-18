using System.Collections.Generic;
using System.Globalization;
using Harbor.Ui.Framework.Panels;
using Harbor.Ui.Framework.State;
using Harbor.Tui.SpectreTui.View;
using Spectre.Console;
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
        var p = new Paragraph().Alignment(Justify.Left);
        p.Lines.Add(TextLine.FromMarkup("[bold cyan]Token Breakdown[/]"));
        p.Lines.Add(TextLine.FromMarkup("[grey]─────────────────────────────[/]"));

        long input = ctx.State.Cost.TokensIn;
        long output = ctx.State.Cost.TokensOut;
        decimal cost = ctx.State.Cost.CostUsd;

        // Cumulative totals.
        p.Lines.Add(TextLine.FromMarkup(
            $"  [green]in[/]   {Format(input).PadLeft(12)}  {Bar(input, ctx.Width - 24, scale: MaxOf(input, output))}"));
        p.Lines.Add(TextLine.FromMarkup(
            $"  [yellow]out[/]  {Format(output).PadLeft(12)}  {Bar(output, ctx.Width - 24, scale: MaxOf(input, output))}"));

        p.Lines.Add(TextLine.FromMarkup("[grey]─────────────────────────────[/]"));
        p.Lines.Add(TextLine.FromMarkup(
            $"  [bold]total[/] {Format(input + output).PadLeft(12)}  [grey]${cost.ToString("F4", CultureInfo.InvariantCulture)}[/]"));
        p.Lines.Add(TextLine.FromMarkup("[grey](cumulative session totals)[/]"));
        return p;
    }

    /// <inheritdoc />
    public bool OnKey(UiKey key, PanelContext ctx) => false;

    private static long MaxOf(long a, long b) => Math.Max(a, Math.Max(b, 1));

    private static string Format(long n) =>
        n >= 1_000_000
            ? (n / 1_000_000.0).ToString("F2", CultureInfo.InvariantCulture) + "M"
            : n >= 1_000
                ? (n / 1_000.0).ToString("F1", CultureInfo.InvariantCulture) + "K"
                : n.ToString(CultureInfo.InvariantCulture);

    private static string Bar(long value, int width, long scale)
    {
        if (width <= 0) return string.Empty;
        int filled = (int)((double)value / scale * width);
        if (filled > width) filled = width;
        if (filled < 0) filled = 0;
        return new string('█', filled) + new string('░', width - filled);
    }
}
