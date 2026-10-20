// #1621: the engine-only half of ScreenBuffer.
//
// `ContentEquals` needs `CellSimd` (the vectorized cell comparison, PF1,
// issue #1237), which the shared vocabulary has no counterpart for — so this
// member cannot live in the copied file. It lives here instead, and the class
// is `partial` on both sides so that the shared twin of ScreenBuffer.cs stays
// a verbatim copy of the engine's file (guarded by
// CellForgeEngineVocabularyParityRules).

namespace Harbor.Tui.CellForge.Rendering;

/// <summary>
///     The engine-only part of <see cref="ScreenBuffer" />: the members that
///     depend on engine-internal machinery and therefore have no shared
///     counterpart.
/// </summary>
public sealed partial class ScreenBuffer
{
    /// <summary>
    /// Vectorized content equality (PF1, issue #1237): same contract as the
    /// per-cell <c>Get(x,y) != Get(x,y)</c> loop in
    /// <see cref="DiffEngine.FrontMatches"/>, over the dense
    /// <c>Cols*Rows</c> prefix via <see cref="CellSimd"/>.
    /// </summary>
    internal bool ContentEquals(ScreenBuffer other)
    {
        if (Cols != other.Cols || Rows != other.Rows)
        {
            return false;
        }

        int count = Cols * Rows;
        if (count == 0)
        {
            return true;
        }

        return CellSimd.SequenceEqual(
            _cells.AsSpan(0, count),
            other._cells.AsSpan(0, count));
    }
}
