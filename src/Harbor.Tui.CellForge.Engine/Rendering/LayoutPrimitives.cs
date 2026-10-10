// #436: engine-owned port of the two shared layout primitives declared in
// src/Harbor.Tui.CellForge/Chat/Rendering/LayoutTree.cs — verbatim except the
// header. The engine is a standalone leaf (zero Harbor references); these live
// here now.
//
// WHY A PORT AND NOT A DUPLICATION: there is exactly ONE home. The Chat-side
// declarations are deleted in the same slice, so Chat (which references the
// engine) binds these. The downward-move alternative — a neutral assembly for
// two layout primitives — was evaluated and rejected: the engine already owns
// the sibling vocabulary (Rect, Cell, ScreenBuffer) these signatures speak,
// so a third assembly would add an edge to every consumer to house two types.
// See CellForgeEngineAtomicityRules, which guards the achieved state.

namespace Harbor.Tui.CellForge.Rendering;

/// <summary>Splits a node's usable extent along its main axis.</summary>
public enum SplitDir : byte
{
    /// <summary>Children share the width (side-by-side columns).</summary>
    Horizontal = 0,

    /// <summary>Children share the height (stacked rows).</summary>
    Vertical = 1,
}

/// <summary>Immutable size pair for panel minimums.</summary>
public readonly record struct Size(int Width, int Height);
