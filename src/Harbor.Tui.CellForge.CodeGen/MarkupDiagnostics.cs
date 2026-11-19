using System;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace Harbor.Tui.CellForge.CodeGen;

/// <summary>
///     Bridges parser diagnostics into Roslyn for issue #2431. Each bad
///     <c>.harbor-ui</c> line becomes an error with an <c>HZxxxx</c> id and a
///     location in the source file, so a bad markup document fails the build
///     at the line the author can see.
/// </summary>
/// <remarks>
///     The location is built from the parser's 1-based line and column. The
///     offset is derived from the source text, which the incremental pipeline
///     already carries; no syntax tree is needed. A zero-length span is
///     enough to put the squiggle on the offending character.
/// </remarks>
internal static class MarkupDiagnostics
{
    /// <summary>The id reported for a syntax error in a markup file.</summary>
    internal const string SyntaxErrorId = "HZ0001";

    /// <summary>The id reported for a cycle in the module import graph.</summary>
    internal const string CyclicImportId = "HZ0002";

    /// <summary>The id reported for an import that does not resolve to a module.</summary>
    internal const string MissingImportId = "HZ0003";

    private const string Category = "CellForge.Markup";

    private static readonly DiagnosticDescriptor SyntaxRule = new(
        id: SyntaxErrorId,
        title: "Invalid CellForge markup",
        messageFormat: "{0}:{1}: {2}",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "The .harbor-ui parser rejected this line; no generated source was emitted.");

    private static readonly DiagnosticDescriptor CyclicRule = new(
        id: CyclicImportId,
        title: "Cyclic CellForge markup import",
        messageFormat: "{0}:{1}: {2}",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "The .harbor-ui import graph contains a cycle; no source was emitted for the cycle.");

    private static readonly DiagnosticDescriptor MissingRule = new(
        id: MissingImportId,
        title: "Unresolved CellForge markup import",
        messageFormat: "{0}:{1}: {2}",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "The .harbor-ui import does not name a file in this compilation.");

    /// <summary>Turns one parser diagnostic into a compiler error.</summary>
    /// <param name="path">Project-relative path used as the location's file name.</param>
    /// <param name="text">The markup source, used to resolve the character offset.</param>
    /// <param name="diagnostic">The parser's finding.</param>
    internal static Diagnostic SyntaxError(string path, string text, MarkupDiagnostic diagnostic) =>
        Diagnostic.Create(
            SyntaxRule,
            CreateLocation(path, text, diagnostic.Line, diagnostic.Column),
            diagnostic.Line,
            diagnostic.Column,
            diagnostic.Message);

    /// <summary>Turns one graph finding into a compiler error at its import line.</summary>
    /// <param name="id">The HZ code for this finding.</param>
    /// <param name="path">Project-relative path used as the location's file name.</param>
    /// <param name="text">The importing file's source, used for the offset.</param>
    /// <param name="diagnostic">Position and message of the finding.</param>
    internal static Diagnostic GraphError(string id, string path, string text, MarkupDiagnostic diagnostic) =>
        Diagnostic.Create(
            id == CyclicImportId ? CyclicRule : MissingRule,
            CreateLocation(path, text, diagnostic.Line, diagnostic.Column),
            diagnostic.Line,
            diagnostic.Column,
            diagnostic.Message);

    private static Location CreateLocation(string path, string text, int line, int column)
    {
        var lineNumber = Math.Max(1, line);
        var columnNumber = Math.Max(1, column);

        var offset = 0;
        for (var current = 1; current < lineNumber && offset < text.Length; current++)
        {
            var newline = text.IndexOf('\n', offset);
            if (newline < 0)
            {
                offset = text.Length;
                break;
            }

            offset = newline + 1;
        }

        offset = Math.Min(text.Length, offset + columnNumber - 1);
        var position = new LinePosition(lineNumber - 1, columnNumber - 1);
        return Location.Create(
            path,
            new TextSpan(offset, 0),
            new LinePositionSpan(position, position));
    }
}
