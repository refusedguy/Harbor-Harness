using System.Text.RegularExpressions;

namespace Harbor.Application.Diagnostics;

/// <summary>
///     Classifies a tool's textual output into <see cref="DiagnosticIssue" />
///     rows tagged <see cref="DiagnosticIssueSource.ToolOutput" />.
/// </summary>
/// <remarks>
///     <para>
///         <b>Why this is here and not in a renderer.</b> Deciding that
///         <c>error CS0246: …</c> is an error is COUNTING, and the headless core
///         counts. #674 found the same six patterns living in
///         <c>src/Harbor.Ui.Framework.Projection/Projection/PanelExtractors.cs</c>,
///         where they parsed the <c>bash</c> tool's output and fed the result
///         into the same panel that was supposed to be showing language-server
///         diagnostics — which is why that panel had two unrelated things
///         summed under one heading. The patterns moved here verbatim: the
///         architecture guard reads them back out of THIS file and fails if the
///         identical pattern is declared anywhere else under <c>src/</c> or
///         <c>apps/</c>.
///     </para>
///     <para>
///         <b>What this is NOT.</b> This is not a substitute for
///         <see cref="Abstractions.Lsp.ILspService" />. A language server
///         reports with the protocol's own severity and real file/line — that
///         path has no pattern matching anywhere and never did. There is no core
///         API that reports «something failed inside <c>npm test</c>», which is
///         the only reason a text detector exists at all; it is a supplement,
///         and it is tagged as one so no renderer can present it as LSP data.
///     </para>
///     <para>
///         <b>What it cannot catch.</b> A classifier that shares no pattern with
///         this table is invisible to the guard that keeps this table single.
///         «Is this a classifier?» is a judgement, not a shape; the guard states
///         its own limit rather than implying more.
///     </para>
/// </remarks>
public static partial class ToolOutputIssueDetector
{
    /// <summary>
    ///     Most issues kept from one tool result. A build log can be megabytes
    ///     long, and every kept row is copied into the immutable UI snapshot on
    ///     the next push; without a floor a single runaway log turns the
    ///     diagnostics list into a second transcript.
    /// </summary>
    public const int MaxIssuesPerOutput = 50;

    /// <summary>
    ///     Issues classified out of <paramref name="output" />, in line order,
    ///     capped at <see cref="MaxIssuesPerOutput" />. Empty when nothing
    ///     matched — «no detector fired» is the honest answer for a clean log,
    ///     and callers that need a fallback (an agent-level error, say) decide
    ///     their own rather than have this invent one.
    /// </summary>
    /// <param name="producer">Recorded on every row, so the UI can say what spoke.</param>
    /// <param name="output">Raw tool output, exactly as the tool returned it.</param>
    public static IReadOnlyList<DiagnosticIssue> Detect(string producer, string output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return [];
        }

        List<DiagnosticIssue>? found = null;
        string[] rows = output.Split('\n');

        for (int i = 0; i < rows.Length; i++)
        {
            if (found is not null && found.Count >= MaxIssuesPerOutput)
            {
                break;
            }

            string row = rows[i].Trim();
            if (row.Length == 0 || !TryClassify(row, out string name, out DiagnosticIssueSeverity severity))
            {
                continue;
            }

            found ??= [];
            found.Add(new DiagnosticIssue(
                DiagnosticIssueSource.ToolOutput,
                severity,
                name.Length == 0 ? producer : name,
                null,
                0,
                row));
        }

        if (found is null)
        {
            return [];
        }

        return found;
    }

    /// <summary>
    ///     First matching detector wins, in a fixed order, and the severity is
    ///     decided by the row itself rather than by which detector fired: a
    ///     <c>warning:</c>-prefixed C# diagnostic is a warning, whatever the
    ///     <c>CS####</c> pattern says.
    /// </summary>
    private static bool TryClassify(string row, out string name, out DiagnosticIssueSeverity severity)
    {
        severity = WarningPattern().IsMatch(row)
            ? DiagnosticIssueSeverity.Warning
            : DiagnosticIssueSeverity.Error;

        if (CSharpPattern().IsMatch(row))
        {
            name = "csharp";
            return true;
        }

        if (RustPattern().IsMatch(row))
        {
            name = "rust";
            return true;
        }

        if (PythonPattern().IsMatch(row))
        {
            name = "python";
            return true;
        }

        if (NodePattern().IsMatch(row))
        {
            name = "node";
            return true;
        }

        if (ExceptionPattern().IsMatch(row))
        {
            name = "exception";
            return true;
        }

        if (WarningPattern().IsMatch(row))
        {
            name = "warning";
            severity = DiagnosticIssueSeverity.Warning;
            return true;
        }

        name = string.Empty;
        return false;
    }

    // ── The table. Read back out of this file by DiagnosticsClassificationRule
    //    (issue #674) — a new detector added here is guarded the day it lands.

    /// <summary>C# / MSBuild: <c>CS0246</c>, <c>MSB3021</c>.</summary>
    [GeneratedRegex(@"\b(CS|MSB)\d{4}\b")]
    private static partial Regex CSharpPattern();

    /// <summary>Rust: <c>error[E0308]</c>.</summary>
    [GeneratedRegex(@"error\[E\d+\]")]
    private static partial Regex RustPattern();

    /// <summary>Python traceback header: <c>File "app.py", line 10</c>.</summary>
    [GeneratedRegex(@"File\s+""[^""]+"",\s*line\s+\d+")]
    private static partial Regex PythonPattern();

    /// <summary>Node: a stack frame <c>at … .js:12</c>, or a <c>TypeError:</c>-style name.</summary>
    [GeneratedRegex(@"(\bat\s+.*\.m?jsx?:\d+|\b(Type|Reference|Syntax)Error\s*:)")]
    private static partial Regex NodePattern();

    /// <summary>Any <c>*Exception</c> word.</summary>
    [GeneratedRegex(@"\b\w*exception\b", RegexOptions.IgnoreCase)]
    private static partial Regex ExceptionPattern();

    /// <summary>A bare <c>warning</c> word.</summary>
    [GeneratedRegex(@"\bwarning\b", RegexOptions.IgnoreCase)]
    private static partial Regex WarningPattern();
}
