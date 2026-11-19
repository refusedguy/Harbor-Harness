namespace Harbor.Abstractions.Dsl;

/// <summary>
///     One node of the <c>.harbor-ui</c> module graph: a stable module name,
///     the arena its own source parsed into, and the resolved names of the
///     modules it imports.
/// </summary>
/// <param name="Path">Stable, normalized module name.</param>
/// <param name="Arena">The arena parsed from this module's own text.</param>
/// <param name="Imports">Resolved module names this module imports, in source order.</param>
internal sealed record SpecModule(
    string Path,
    SpecArena Arena,
    IReadOnlyList<SpecImport> Imports);
