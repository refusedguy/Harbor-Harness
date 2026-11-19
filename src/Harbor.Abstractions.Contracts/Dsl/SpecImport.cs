namespace Harbor.Abstractions.Dsl;

/// <summary>
///     One <c>import</c> directive from a markup module. The path is the raw
///     text as written in the source; the module resolver turns it into a
///     stable, project-relative module name before it enters the graph.
/// </summary>
/// <param name="Path">Raw path written after the <c>import</c> keyword.</param>
/// <param name="Line">One-based source line of the directive.</param>
/// <param name="Column">One-based source column of the directive.</param>
internal readonly record struct SpecImport(string Path, int Line, int Column);
