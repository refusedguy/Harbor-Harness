using Harbor.Tui.SpectreTui.View;
using Harbor.Ui.Framework.Panels;
using Harbor.Ui.Framework.Projection;
using Harbor.Ui.Framework.State;
using Harbor.Abstractions.Models;
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
///         <b>Decoupling:</b> reads only from <see cref="UiState.Chat.Lines" />. Does
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
        _cursor = diagnostics.Count == 0 ? 0 : Math.Clamp(_cursor, 0, diagnostics.Count - 1);
        var rows = PanelRows.DiagnosticsRows(diagnostics, _cursor, ctx.Width, ctx.Height);

        var p = new Paragraph().Alignment(Justify.Left);
        foreach (string row in rows)
            p.Lines.Add(TextLine.FromMarkup(StyleRow(row)));
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

    private static string StyleRow(string row)
    {
        if (row.StartsWith("Diagnostics (", StringComparison.Ordinal))
        {
            int paren = row.IndexOf('(');
            string tail = paren >= 0 ? row[paren..] : string.Empty;
            return "[bold cyan]Diagnostics[/] [grey]" + ChatMarkup.Escape(tail) + "[/]";
        }

        if (row == PanelText.Separator)
            return "[grey]" + PanelText.Separator + "[/]";

        if (row.Length >= 4 && (row[0] == '>' || row[0] == ' ') &&
            (row[2] == '✗' || row[2] == '▲'))
        {
            string prefix = row[0] == '>' ? "[black on aqua] [/]" : " ";
            string icon = row[2] == '✗' ? "[red]✗[/]" : "[yellow]▲[/]";
            return $"{prefix} {icon} {ChatMarkup.Escape(row.Length > 4 ? row[4..] : string.Empty)}";
        }

        if (row is "No diagnostics detected.")
            return "[green]" + ChatMarkup.Escape(row) + "[/]";

        if (row is "j/k move" or "Errors emitted by the `bash` tool will show up here.")
            return "[grey]" + ChatMarkup.Escape(row) + "[/]";

        return ChatMarkup.Escape(row);
    }
}
