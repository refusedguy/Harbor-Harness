using Harbor.Tui.SpectreTui.View;
using Harbor.Ui.Framework.Panels;
using Harbor.Ui.Framework.Projection;
using Harbor.Ui.Framework.State;
using Spectre.Tui;
namespace Harbor.Tui.SpectreTui.Panels.Builtin;
/// <summary>
///     Builtin panel that collects errors from <c>bash</c> tool outputs and shows
///     them in a clickable list. Detects:
///     <list type="bullet">
///         <item>C# / .NET compiler diagnostics (CS####, MSB####).</item>
///         <item>Python tracebacks (File "...", line N, ...).</item>
///         <item>Rust compiler errors (error[E####]).</item>
///         <item>Generic stack traces (Exception: ... at ...).</item>
///     </list>
/// </summary>
/// <remarks>
///     <para>
///         <b>Navigation:</b> <c>j/k</c> moves between diagnostics, <c>Enter</c>
///         scrolls the chat transcript to the source line (best-effort — by
///         dispatching <c>ScrollTop</c> / <c>ScrollBottom</c> through the
///         <c>UiStore</c>).
///     </para>
///     <para>
///         <b>Decoupling:</b> reads only from <see cref="UiState.Lines" />. Does
///         not parse files itself — relies on the bash tool output being already in
///         the transcript.
///     </para>
/// </remarks>
public sealed class DiagnosticsPanel : IPanelProvider
{
    private int _cursor;

    /// <inheritdoc />
    public string Id => "diagnostics";

    /// <inheritdoc />
    public string Title => "Diagnostics";

    /// <inheritdoc />
    public TuiPanelPlacement DefaultPlacement => TuiPanelPlacement.Bottom;

    /// <inheritdoc />
    public int DefaultSize => 10;

    /// <inheritdoc />
    public object? Build(PanelContext ctx)
    {
        var diagnostics = PanelExtractors.CollectDiagnostics(ctx.State);

        var p = new Paragraph().Alignment(Justify.Left);
        p.Lines.Add(TextLine.FromMarkup(
            $"[bold cyan]Diagnostics[/] [grey]({diagnostics.Count} issue(s))[/]"));
        p.Lines.Add(TextLine.FromMarkup("[grey]─────────────────────────────[/]"));

        if (diagnostics.Count == 0)
        {
            p.Lines.Add(TextLine.FromMarkup("[green]No diagnostics detected.[/]"));
            p.Lines.Add(TextLine.FromMarkup(
                "[grey]Errors emitted by the `bash` tool will show up here.[/]"));
            return p;
        }

        int maxVisible = Math.Max(2, ctx.Height - 4);
        int start = Math.Max(0, _cursor - maxVisible + 1);
        int end = Math.Min(diagnostics.Count, start + maxVisible);

        for (int i = start; i < end; i++)
        {
            var d = diagnostics[i];
            bool selected = i == _cursor;
            string icon = d.Severity switch
            {
                PanelDiagnosticSeverity.Error => "[red]✗[/]",
                PanelDiagnosticSeverity.Warning => "[yellow]▲[/]",
                _ => "[grey]·[/]"
            };
            string prefix = selected ? "[black on aqua] [/]" : " ";
            string body = ChatMarkup.Escape(Truncate(d.Message, ctx.Width - 8));
            p.Lines.Add(TextLine.FromMarkup($"{prefix} {icon} [grey]{d.Source,-10}[/] {body}"));
        }

        p.Lines.Add(TextLine.FromMarkup("[grey]─────────────────────────────[/]"));
        p.Lines.Add(TextLine.FromMarkup("[grey]j/k move · Enter scroll chat to source[/]"));
        return p;
    }

    /// <inheritdoc />
    public bool OnKey(UiKey key, PanelContext ctx)
    {
        if (key.Code != UiKeyCode.Char || key.Character is null)
            return false;

        switch (key.Character)
        {
            case 'j':
            case 'J':
                _cursor++;
                return true;
            case 'k':
            case 'K':
                _cursor = Math.Max(0, _cursor - 1);
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
