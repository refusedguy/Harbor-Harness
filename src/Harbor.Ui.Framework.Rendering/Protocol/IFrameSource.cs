// IFrameSource.cs: the narrow port between a frame that OWNS its cells and
// the portable cell-diff protocol that READS them (issue #1519).
//
// THE DEPENDENCY THIS FILE EXISTS TO KEEP HONEST
// ----------------------------------------------
// The engine-linked diff encoder bridged the two cell vocabularies by
// copying cells one at a time and transferring the per-cell diff directive
// by NUMERIC CAST: `(UIR.CellDiffOption)(byte)`. A numeric cast is a claim
// that two enums declare the same members in the same order, and nothing in
// the tree checked the claim. The two `CellDiffOption` declarations are
// separate types in separate assemblies (the engine is a zero-Harbor-
// reference leaf since #436, so the shared layer holds a copy), and a
// member added to one of them is invisible to the other: the cast would
// carry it as a DIFFERENT member, silently.
//
// This file is the port that makes the claim unnecessary. The read side (the
// diff and encode side) asks a frame for its dimensions and its cells, and
// nothing else: it never learns which buffer holds the frame, so the two
// sides vary independently. The frame's own side answers in the Rendering
// vocabulary, which is where the translation belongs: one table, in one
// place, spelled out member by member.
//
// WHERE THE PORT LIVES, AND WHY NOT IN THE ENGINE
// ----------------------------------------------
// The port lives with the abstraction it serves (the portable protocol), and
// the implementation lives with the frame it reads. The engine cannot
// implement a port declared here: #436 made it a standalone leaf with zero
// Harbor references, and that isolation is guarded
// (`CellForgeEngineAtomicityRules`). So the implementor sits in the adapter
// assembly that is allowed to see both sides
// (`Harbor.Tui.CellForge`, `EngineFrameSource`), and this file holds the
// contract and the data structure that crosses it.
//
// The direction is one way: the engine does not reference this package, and
// this package does not reference the engine. The adapter sits between them.

namespace Harbor.Ui.Framework.Rendering.Protocol;

/// <summary>
///     A narrow, read-only view of one painted frame: the port through which a
///     diff or encode side reads a frame it does not own.
/// </summary>
/// <remarks>
///     <para>
///         The consumer asks for dimensions and one cell at a time. That is
///         deliberately all: a bulk copy or a precomputed hash would be
///         faster, and both would put the frame's storage layout into the
///         contract, and the layout is exactly what the two sides are allowed
///         to differ about.
///     </para>
///     <para>
///         An implementation answers in the Rendering vocabulary (see
///         <see cref="FrameCell" />), so the consumer never has to know which
///         buffer holds the frame. The CellForge engine's front buffer is
///         read through this port by
///         <c>Harbor.Tui.CellForge.Rendering.EngineFrameSource</c>.
///     </para>
/// </remarks>
public interface IFrameSource
{
    /// <summary>Width of the frame, in cells.</summary>
    int Cols { get; }

    /// <summary>Height of the frame, in cells.</summary>
    int Rows { get; }

    /// <summary>
    ///     Reads the cell at (<paramref name="x" />, <paramref name="y" />).
    /// </summary>
    /// <param name="x">Column, zero-based.</param>
    /// <param name="y">Row, zero-based.</param>
    /// <returns>The cell and the two values that live beside it.</returns>
    FrameCell Get(int x, int y);
}

/// <summary>
///     One cell of a frame, in the Rendering vocabulary: the cell itself plus
///     the two values that live in the frame's side table rather than in the
///     cell.
/// </summary>
/// <param name="Cell">The cell's content and style.</param>
/// <param name="DiffOption">
///     The per-cell diff directive (the side table), which is not part of the
///     cell's five-compare equality and travels beside it.
/// </param>
/// <param name="ForcedWidth">
///     The explicit cursor advance, zero when the cell has none.
/// </param>
public readonly record struct FrameCell(Cell Cell, CellDiffOption DiffOption, ushort ForcedWidth);
