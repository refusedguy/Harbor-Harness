// EngineFrameSource.cs: the engine's front buffer behind the narrow
// frame-source port (issue #1519).
//
// WHAT THIS IS
// ------------
// The implementor half of the bridge between the CellForge engine and the
// portable cell-diff protocol. The engine owns the frame; the protocol reads
// it; this class is the one place in the tree that knows both, and the one
// place where the engine's cell vocabulary is translated into the Rendering
// one.
//
// The translation is explicit, member by member. The diff directive used to
// cross as `(UIR.CellDiffOption)(byte)`, a numeric cast that claimed the two
// enums declare the same members in the same order and checked nothing: the
// two declarations are separate types in separate assemblies, so a member
// added to one crossed as a DIFFERENT member of the other, silently. The
// table below names both sides, and the census in
// `tests/Harbor.Tui.CellForge.Tests/CellForgeDiffEncoderBridgeTests.cs`
// walks `Enum.GetValues` over the engine's enum, so a member added later is
// a red row naming the file to edit.
//
// WHY THE IMPLEMENTOR IS HERE AND NOT IN THE ENGINE
// --------------------------------------------------
// #436 made the engine a standalone leaf: zero Harbor references, zero
// Harbor imports, guarded by `CellForgeEngineAtomicityRules`. The port is
// declared in the shared rendering layer, so the engine cannot implement it.
// The implementor therefore lives in the adapter assembly, which is the one
// place allowed to see both sides, and the port lives with the abstraction it
// serves (`Harbor.Ui.Framework.Rendering.Protocol.IFrameSource`).

namespace Harbor.Tui.CellForge.Rendering;

using Harbor.Ui.Framework.Rendering.Protocol;
using Ensure = Harbor.Abstractions.Extensions.Ensure;
using EngineCells = Harbor.Tui.CellForge.Rendering;
using UIR = Harbor.Ui.Framework.Rendering;

/// <summary>
///     The CellForge engine's front buffer, seen through the narrow
///     frame-source port: the implementor half of the bridge between the
///     engine and the portable cell-diff protocol.
/// </summary>
/// <remarks>
///     <para>
///         The engine and the shared rendering layer keep their own copies of
///         the cell vocabulary (the engine is a leaf and cannot borrow, see
///         #436 and #1621). This class is where the two meet, so it is also
///         where a change to either copy has to be noticed: the cell is
///         rebuilt field by field, and the diff directive is translated
///         member by member.
///     </para>
///     <para>
///         One instance reads one frame. It holds no state beyond the buffer
///         it was handed, so a caller that reads a new frame makes a new
///         instance rather than reusing one across frames (the engine can
///         swap its front buffer).
///     </para>
/// </remarks>
public sealed class EngineFrameSource : IFrameSource
{
    private readonly EngineCells.ScreenBuffer _frame;

    /// <summary>
    ///     Wraps the engine buffer the frame is read from.
    /// </summary>
    /// <param name="frame">The engine's front buffer.</param>
    public EngineFrameSource(EngineCells.ScreenBuffer frame) =>
        _frame = Ensure.NotNull(frame);

    /// <inheritdoc />
    public int Cols => _frame.Cols;

    /// <inheritdoc />
    public int Rows => _frame.Rows;

    /// <inheritdoc />
    public FrameCell Get(int x, int y)
    {
        EngineCells.Cell cell = _frame.Get(x, y);
        return new FrameCell(
            UIR.Cell.FromRaw(cell.Rune, cell.Fg, cell.Bg, cell.Flags, cell.Width),
            MapDiffOption(_frame.GetDiffOption(x, y)),
            _frame.GetForcedWidth(x, y));
    }

    /// <summary>
    ///     The diff-directive table: the engine's member on the left, the
    ///     Rendering member on the right, one line each.
    /// </summary>
    /// <param name="option">The engine's directive.</param>
    /// <returns>The same directive in the Rendering vocabulary.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    ///     The value is not a declared member of the engine's enum.
    /// </exception>
    internal static UIR.CellDiffOption MapDiffOption(EngineCells.CellDiffOption option) => option switch
    {
        EngineCells.CellDiffOption.None => UIR.CellDiffOption.None,
        EngineCells.CellDiffOption.Skip => UIR.CellDiffOption.Skip,
        EngineCells.CellDiffOption.AlwaysUpdate => UIR.CellDiffOption.AlwaysUpdate,
        EngineCells.CellDiffOption.ForcedWidth => UIR.CellDiffOption.ForcedWidth,

        // The discard arm has to be here and it has to THROW. C# cannot
        // express "exhaustive over the NAMED members" of an enum: a
        // discard-free switch is rejected with CS8524, because
        // `(CellDiffOption)7` stays constructible even when every declared
        // member is listed. A discard that ANSWERS is what the numeric cast
        // was: it invented a member nobody declared and carried it across
        // unnoticed. So the gate for a NEW member is the reflection census in
        // `CellForgeDiffEncoderBridgeTests` (it walks `Enum.GetValues` over
        // the engine's enum and names the member that has no row here).
        _ => throw new ArgumentOutOfRangeException(
            nameof(option),
            option,
            "No Rendering diff directive is declared for this engine value."),
    };
}
