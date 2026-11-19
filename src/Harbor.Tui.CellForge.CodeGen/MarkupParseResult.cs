using Harbor.Abstractions.Dsl;

namespace Harbor.Tui.CellForge.CodeGen;

/// <summary>
///     Result of one parse. The arena is always populated with whatever the
///     parser understood; the diagnostics say where the rest was rejected.
/// </summary>
public sealed class MarkupParseResult
{
    /// <summary>Creates a parse result without a state annotation.</summary>
    /// <param name="arena">Nodes the parser built.</param>
    /// <param name="diagnostics">Syntax errors, empty on success.</param>
    public MarkupParseResult(SpecArena arena, IReadOnlyList<MarkupDiagnostic> diagnostics)
        : this(arena, diagnostics, null, 0)
    {
    }

    /// <summary>Creates a parse result with a state annotation.</summary>
    /// <param name="arena">Nodes the parser built.</param>
    /// <param name="diagnostics">Syntax errors, empty on success.</param>
    /// <param name="stateType">Type named by a <c>state:</c> directive, if any.</param>
    /// <param name="stateTypeLine">One-based line of the directive.</param>
    public MarkupParseResult(
        SpecArena arena,
        IReadOnlyList<MarkupDiagnostic> diagnostics,
        string? stateType,
        int stateTypeLine)
        : this(arena, diagnostics, stateType, stateTypeLine, [])
    {
    }

    /// <summary>Creates a parse result with state and import directives.</summary>
    /// <param name="arena">Nodes the parser built.</param>
    /// <param name="diagnostics">Syntax errors, empty on success.</param>
    /// <param name="stateType">Type named by a <c>state:</c> directive, if any.</param>
    /// <param name="stateTypeLine">One-based line of the directive.</param>
    /// <param name="imports">Raw import directives, in source order.</param>
    internal MarkupParseResult(
        SpecArena arena,
        IReadOnlyList<MarkupDiagnostic> diagnostics,
        string? stateType,
        int stateTypeLine,
        IReadOnlyList<SpecImport> imports)
    {
        Arena = arena ?? throw new ArgumentNullException(nameof(arena));
        Diagnostics = diagnostics ?? throw new ArgumentNullException(nameof(diagnostics));
        StateType = stateType;
        StateTypeLine = stateTypeLine;
        Imports = imports ?? throw new ArgumentNullException(nameof(imports));
    }

    /// <summary>Nodes the parser built.</summary>
    public SpecArena Arena { get; }

    /// <summary>Syntax errors, empty on success.</summary>
    public IReadOnlyList<MarkupDiagnostic> Diagnostics { get; }

    /// <summary>Type named by a <c>state:</c> directive, or null when absent.</summary>
    public string? StateType { get; }

    /// <summary>One-based line of the <c>state:</c> directive, or zero when absent.</summary>
    public int StateTypeLine { get; }

    /// <summary>Raw <c>import</c> directives, in source order.</summary>
    internal IReadOnlyList<SpecImport> Imports { get; }

    /// <summary>Whether the parser accepted the whole file.</summary>
    public bool IsValid => Diagnostics.Count == 0;
}
