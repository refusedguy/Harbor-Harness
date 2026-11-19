namespace Harbor.Abstractions.Dsl;

/// <summary>
///     One back edge found while walking the import graph. The position comes
///     from the <c>import</c> directive that closes the cycle, so a diagnostic
///     can point at the line the author can edit.
/// </summary>
/// <param name="Chain">Module names from the repeated module around to itself.</param>
/// <param name="ImportPath">Resolved module name of the closing edge.</param>
/// <param name="Line">One-based line of the closing import directive.</param>
/// <param name="Column">One-based column of the closing import directive.</param>
/// <param name="Message">Human-readable description of the cycle.</param>
internal sealed record SpecImportCycle(
    IReadOnlyList<string> Chain,
    string ImportPath,
    int Line,
    int Column,
    string Message);
