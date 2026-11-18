using Harbor.Abstractions.Dsl;

namespace Harbor.Tui.CellForge.CodeGen;

/// <summary>
///     Result of one parse. The arena is always populated with whatever the
///     parser understood; the diagnostics say where the rest was rejected.
/// </summary>
public sealed class MarkupParseResult
{
    /// <summary>Creates a parse result.</summary>
    /// <param name="arena">Nodes the parser built.</param>
    /// <param name="diagnostics">Syntax errors, empty on success.</param>
    public MarkupParseResult(SpecArena arena, IReadOnlyList<MarkupDiagnostic> diagnostics)
    {
        Arena = arena ?? throw new ArgumentNullException(nameof(arena));
        Diagnostics = diagnostics ?? throw new ArgumentNullException(nameof(diagnostics));
    }

    /// <summary>Nodes the parser built.</summary>
    public SpecArena Arena { get; }

    /// <summary>Syntax errors, empty on success.</summary>
    public IReadOnlyList<MarkupDiagnostic> Diagnostics { get; }

    /// <summary>Whether the parser accepted the whole file.</summary>
    public bool IsValid => Diagnostics.Count == 0;
}
