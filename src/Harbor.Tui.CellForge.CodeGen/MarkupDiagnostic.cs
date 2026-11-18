namespace Harbor.Tui.CellForge.CodeGen;

/// <summary>One syntax error found while parsing a <c>.harbor-ui</c> file.</summary>
/// <param name="Line">One-based line in the source file.</param>
/// <param name="Column">One-based column in the source file.</param>
/// <param name="Message">What the parser rejected.</param>
public readonly record struct MarkupDiagnostic(int Line, int Column, string Message);
