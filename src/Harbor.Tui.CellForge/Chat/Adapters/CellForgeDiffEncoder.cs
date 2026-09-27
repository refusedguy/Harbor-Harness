namespace Harbor.Tui.CellForge.Rendering;

using Harbor.Ui.Framework.Rendering;
using Harbor.Ui.Framework.Rendering.Protocol;

/// <summary>
///     CellForge-side factory for the portable cell-diff protocol
///     (renderer-unification sprint Phase 6.2). The single
///     <see cref="ICellDiffEncoder" /> implementation is
///     <see cref="RowHashDiffEncoder" /> — this factory only binds it to a
///     <see cref="DiffEngine" />'s already-shown front buffer for the
///     engine-linked mode, without touching the ANSI path: the
///     <see cref="DiffEngine"/> SGR automaton stays untouched (hard rule 5);
///     the encoder reads the engine's front through its public surface and
///     encodes the delta against it.
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
/// </remarks>
public static class CellForgeDiffEncoder
{
    /// <summary>Standalone encoder — callers supply both frames.</summary>
    public static ICellDiffEncoder Create() => new RowHashDiffEncoder();

    /// <summary>
    ///     Engine-linked encoder — diffs against the engine's current front
    ///     buffer (the last frame it flushed).
    /// </summary>
    public static EngineLinkedEncoder CreateEngineLinked(DiffEngine engine) =>
        new(engine ?? throw new ArgumentNullException(nameof(engine)));

    /// <summary>
    ///     Encodes the delta from <paramref name="engine"/>'s front buffer
    ///     (the last frame the ANSI path flushed) to <paramref name="next"/>.
    /// </summary>
    public static CellDiffBatch EncodeFromEngineFront(
        DiffEngine engine,
        ScreenBuffer next,
        IReadOnlyList<Rect>? hints,
        long sequence)
    {
        ArgumentNullException.ThrowIfNull(engine);
        return new RowHashDiffEncoder().Encode(engine.Front, next, hints, sequence);
    }

    /// <summary>
    ///     <see cref="ICellDiffEncoder" /> bound to a <see cref="DiffEngine" />'s
    ///     front buffer. <see cref="EncodeCellForge" /> diffs the engine's
    ///     last-flushed frame against <c>next</c>; <see cref="Encode" /> is the
    ///     plain explicit-pair contract (delegated to the shared portable
    ///     encoder).
    /// </summary>
    public sealed class EngineLinkedEncoder : ICellDiffEncoder
    {
        private readonly DiffEngine _engine;
        private readonly RowHashDiffEncoder _portable = new();

        public EngineLinkedEncoder(DiffEngine engine) =>
            _engine = engine ?? throw new ArgumentNullException(nameof(engine));

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
        ///     frame the ANSI path flushed) to <paramref name="next"/>.
        /// </summary>
        public CellDiffBatch EncodeCellForge(
            ScreenBuffer next,
            IReadOnlyList<Rect>? hints,
            long sequence)
        {
            return _portable.Encode(_engine.Front, next, hints, sequence);
        }
    }
}
