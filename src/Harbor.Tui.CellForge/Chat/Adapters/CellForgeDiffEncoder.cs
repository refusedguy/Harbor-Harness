namespace Harbor.Tui.CellForge.Rendering;

using Harbor.Ui.Framework.Rendering;
using Harbor.Ui.Framework.Rendering.Protocol;

/// <summary>
///     CellForge-side adapter of the portable cell-diff protocol
///     (renderer-unification sprint Phase 6.2). Lets CellForge emit
///     <see cref="CellDiffBatch"/>es — the differential IR any renderer
///     backend can consume — without touching the ANSI path: the
///     <see cref="DiffEngine"/> SGR automaton stays untouched (hard rule 5);
///     this adapter reads the engine's already-shown front buffer through its
///     public surface and encodes the delta against it.
/// </summary>
/// <remarks>
///     <para>
///         Engine-linked mode (recommended for CellForge consumers): after
///         each <see cref="DiffEngine.Flush"/>, the engine's front equals the
///         frame the terminal received. Encoding
///         <c>engine front → next frame</c> therefore yields exactly the
///         batch a parallel differential consumer needs to stay in sync with
///         what the ANSI path painted — e.g. SpectreTui panels, Blazor DOM
///         mirrors, or a remote-process renderer.
///     </para>
///     <para>
///         Standalone mode: encode an explicit prev/next pair (the generic
///         <see cref="ICellDiffEncoder"/> contract).
///     </para>
/// </remarks>
public sealed class CellForgeDiffEncoder : ICellDiffEncoder
{
    /// <summary>Matches <see cref="DiffEngine.HintAreaThreshold"/> by contract.</summary>
    public const double HintAreaThreshold = DiffEngine.HintAreaThreshold;

    private readonly DiffEngine? _engine;
    private readonly RowHashDiffEncoder _portable = new();

    /// <summary>Standalone encoder — callers supply both frames.</summary>
    public CellForgeDiffEncoder()
    {
    }

    /// <summary>
    ///     Engine-linked encoder — <see cref="EncodeCellForge"/> diffs against
    ///     the engine's current front buffer (the last frame it flushed).
    /// </summary>
    public CellForgeDiffEncoder(DiffEngine engine)
    {
        _engine = engine;
    }

    /// <inheritdoc />
    public CellDiffBatch Encode(
        ScreenBuffer prev,
        ScreenBuffer next,
        IReadOnlyList<Rect>? hints,
        long sequence)
    {
        return _portable.Encode(prev, next, hints, sequence);
    }

    /// <summary>
    ///     Encodes the delta from the linked engine's front buffer (the last
    ///     frame the ANSI path flushed) to <paramref name="next"/>. Throws in
    ///     standalone mode — use <see cref="Encode"/> instead.
    /// </summary>
    public CellDiffBatch EncodeCellForge(
        ScreenBuffer next,
        IReadOnlyList<Rect>? hints,
        long sequence)
    {
        if (_engine is null)
        {
            throw new InvalidOperationException(
                "EncodeCellForge requires an engine-linked CellForgeDiffEncoder.");
        }

        return _portable.Encode(_engine.Front, next, hints, sequence);
    }
}
