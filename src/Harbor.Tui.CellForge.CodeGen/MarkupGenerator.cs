using Microsoft.CodeAnalysis;

namespace Harbor.Tui.CellForge.CodeGen;

/// <summary>
///     Entry point for the CellForge markup generator (issue #2424).
/// </summary>
/// <remarks>
///     Slice 0 establishes the Roslyn component only. The parser/IR slice
///     (#2426) and the <c>BuildRoot</c> emitter (#2428) attach their pipeline
///     stages to <see cref="Initialize"/>; no source is produced today.
/// </remarks>
[Generator]
public sealed class MarkupGenerator : IIncrementalGenerator
{
    /// <inheritdoc />
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        // Intentionally empty: this slice proves the component compiles.
    }
}
