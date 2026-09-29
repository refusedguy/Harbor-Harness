namespace Harbor.Abstractions.Models;

/// <summary>
///     Which headless producer a <see cref="DiagnosticIssue" /> came from.
/// </summary>
/// <remarks>
///     The two are NOT interchangeable, and #674 is why: a projection used to
///     classify the <c>bash</c> tool's textual output with regexes and present
///     the result as «diagnostics», in the same panel as — and standing in for
///     — the language server's. The server reports with the protocol's own
///     severity and real file/line; a log line is a line of text that matched
///     a pattern. Keeping them in one list with an explicit
///     <see cref="DiagnosticIssue.Source" /> tag lets a renderer section them
///     without ever having to guess which is which.
/// </remarks>
public enum DiagnosticIssueSource : byte
{
    /// <summary>
    ///     Published by a language server through
    ///     <c>textDocument/publishDiagnostics</c> and normalised into
    ///     <see cref="Lsp.LspDiagnostic" /> by the LSP layer. Severity is the
    ///     server's own; there is no pattern matching anywhere on this path.
    /// </summary>
    LanguageServer = 0,

    /// <summary>
    ///     Classified out of a tool's textual output (a build log, a test run).
    ///     There is no core API that reports these, so a detector in the
    ///     headless core extracts them; they carry no line and no file unless
    ///     the producer named one.
    /// </summary>
    ToolOutput = 1,
}

/// <summary>
///     Display-neutral issue severity shared by both producers.
/// </summary>
/// <remarks>
///     Deliberately NOT <c>LspSeverity</c>: that enum is protocol-shaped — it
///     is 1-based so it round-trips the wire format, and it carries a
///     <c>None = 0</c> emergency arm that only exists to keep the numbering
///     aligned. Neither property means anything for a line of a build log. The
///     one mapping between them lives with the aggregator that owns both.
/// </remarks>
public enum DiagnosticIssueSeverity : byte
{
    /// <summary>An error: the run failed, or the server said Error.</summary>
    Error = 0,

    /// <summary>A warning.</summary>
    Warning = 1,

    /// <summary>Informational, not actionable.</summary>
    Information = 2,

    /// <summary>A hint.</summary>
    Hint = 3,
}

/// <summary>
///     One classified issue, as the headless core produced it. This is a DATA
///     type: no producer, no detection, no rendering happens here — a projection
///     receives these already classified and only decides where to draw them.
/// </summary>
/// <param name="Source">Which producer this came from.</param>
/// <param name="Severity">How bad it is.</param>
/// <param name="Producer">
///     Who reported it: a language-server id (<c>csharp</c>, <c>pyright</c>) or
///     a detector name (<c>csharp</c>, <c>rust</c>, <c>python</c>, <c>node</c>,
///     <c>exception</c>, <c>warning</c>) for tool output.
/// </param>
/// <param name="FilePath">The file the issue belongs to, when the producer named one.</param>
/// <param name="Line">1-based line, or <c>0</c> when the producer reported none.</param>
/// <param name="Message">The human-readable text, already formatted by the producer.</param>
public sealed record DiagnosticIssue(
    DiagnosticIssueSource Source,
    DiagnosticIssueSeverity Severity,
    string Producer,
    string? FilePath,
    int Line,
    string Message);
