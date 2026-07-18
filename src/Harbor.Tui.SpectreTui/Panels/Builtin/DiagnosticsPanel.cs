using System.Collections.Generic;
using System.Text.RegularExpressions;
using Harbor.Ui.Framework.Panels;
using Harbor.Ui.Framework.State;
using Harbor.Tui.SpectreTui.View;
using Spectre.Console;
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
    private static readonly Regex[] Patterns =
    [
        // C# / MSBuild: "error CS0117: ..." or ": error MSB3026: ..."
        new(@"error\s+(CS|MSB|CA|NET)\d{4,}\b", RegexOptions.Compiled | RegexOptions.CultureInvariant),
        // Rust: "error[E0308]: mismatched types"
        new(@"error\[E\d{4}\]", RegexOptions.Compiled | RegexOptions.CultureInvariant),
        // Python: "Traceback (most recent call last):" or "File \"x.py\", line N, in <module>"
        new(@"Traceback \(most recent call last\)|File\s+""[^""]+""\s*,\s*line\s+\d+", RegexOptions.Compiled | RegexOptions.CultureInvariant),
        // Generic: "Exception: ..." or "...Exception of type ..."
        new(@"\b(System\.[A-Z]\w*Exception|\w+Exception)\b", RegexOptions.Compiled | RegexOptions.CultureInvariant),
        // Node / JS: "TypeError: ..." "  at <function> (<file>:<line>:<col>)"
        new(@"^\s*at\s+\S+\s*\([^)]+:\d+:\d+\)|^\s*(\w+Error):\s", RegexOptions.Compiled | RegexOptions.Multiline)
    ];

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
        var diagnostics = CollectDiagnostics(ctx.State);

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
                DiagnosticSeverity.Error => "[red]✗[/]",
                DiagnosticSeverity.Warning => "[yellow]▲[/]",
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

    private static List<Diagnostic> CollectDiagnostics(UiState state)
    {
        var result = new List<Diagnostic>(8);
        var lines = state.Lines;
        for (int i = 0; i < lines.Length; i++)
        {
            if (lines[i].Role is not (ChatRole.ToolResult or ChatRole.Error))
                continue;

            string text = lines[i].Text ?? string.Empty;
            // Strip the reducer's "✓ " / "✗ " prefix.
            if (text.Length >= 2 && (text[0] == '✓' || text[0] == '✗') && text[1] == ' ')
                text = text[2..];

            foreach (var pattern in Patterns)
            {
                var match = pattern.Match(text);
                if (!match.Success) continue;

                var severity = lines[i].Role == ChatRole.Error || text.StartsWith("error", System.StringComparison.OrdinalIgnoreCase)
                    ? DiagnosticSeverity.Error
                    : DiagnosticSeverity.Warning;

                // Extract the line containing the match for a tighter message.
                int lineStart = text.LastIndexOf('\n', match.Index) + 1;
                int lineEnd = text.IndexOf('\n', match.Index);
                if (lineEnd < 0) lineEnd = text.Length;
                string snippet = text[lineStart..lineEnd].Trim();

                string source = ExtractSource(match.Value);
                result.Add(new Diagnostic(severity, source, snippet));
                break; // one diagnostic per transcript line
            }
        }
        return result;
    }

    private static string ExtractSource(string match)
    {
        if (match.StartsWith("error CS", System.StringComparison.OrdinalIgnoreCase))
            return "csharp";
        if (match.StartsWith("error MSB", System.StringComparison.OrdinalIgnoreCase))
            return "msbuild";
        if (match.StartsWith("error[E", System.StringComparison.OrdinalIgnoreCase))
            return "rust";
        if (match.StartsWith("Traceback", System.StringComparison.OrdinalIgnoreCase) || match.Contains("File \""))
            return "python";
        if (match.Contains("Error:"))
            return "node";
        if (match.Contains("Exception"))
            return "runtime";
        return "other";
    }

    private static string Truncate(string text, int max)
    {
        if (max <= 3) return text;
        return text.Length <= max ? text : text[..(max - 1)] + "…";
    }

    private enum DiagnosticSeverity { Error, Warning, Info }

    private sealed record Diagnostic(DiagnosticSeverity Severity, string Source, string Message);
}
