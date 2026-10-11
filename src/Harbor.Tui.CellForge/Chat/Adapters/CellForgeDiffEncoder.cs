namespace Harbor.Tui.CellForge.Rendering;

using Harbor.Ui.Framework.Rendering;
using Harbor.Ui.Framework.Rendering.Protocol;

// #436: this adapter straddles the engine/host boundary inside the engine's
// namespace, so bare cell/screen names resolve to the engine's verbatim
// ports. The portable protocol (ICellDiffEncoder/RowHashDiffEncoder) speaks
// the Rendering vocabulary, while DiffEngine.Front is engine-typed — both
// sides are pinned explicitly (EngineCells vs UIR) and the front crosses
// the boundary through a field-for-field snapshot (the reverse of
// ScreenSession.CopyPaintToBack; both cell structs share the 16-byte
// layout, so every field round-trips).
using EngineCells = Harbor.Tui.CellForge.Rendering;
using UIR = Harbor.Ui.Framework.Rendering;

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
    ///     The front crosses into the Rendering vocabulary through a snapshot
    ///     copy; the portable encoder then diffs two Rendering buffers.
    /// </summary>
    public static CellDiffBatch EncodeFromEngineFront(
        EngineCells.DiffEngine engine,
        UIR.ScreenBuffer next,
        IReadOnlyList<UIR.Rect>? hints,
        long sequence)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(next);
        var prev = SnapshotFront(engine.Front);
        return new RowHashDiffEncoder().Encode(prev, next, hints, sequence);
    }

    /// <summary>
    ///     Snapshots an engine front buffer into the Rendering vocabulary:
    ///     exact cell-for-cell copy, then row-hash invalidation so the
    ///     portable encoder rehashes what the copy touched (At() writes do
    ///     not invalidate, same discipline as
    ///     <see cref="Harbor.Tui.CellForge.Streaming.ScreenSession"/>).
    /// </summary>
    private static UIR.ScreenBuffer SnapshotFront(EngineCells.ScreenBuffer front)
    {
        var snap = new UIR.ScreenBuffer(front.Cols, front.Rows);
        CopyEngineToRendering(front, snap);
        return snap;
    }

    private static void CopyEngineToRendering(EngineCells.ScreenBuffer src, UIR.ScreenBuffer dst)
    {
        for (int y = 0; y < src.Rows; y++)
        {
            for (int x = 0; x < src.Cols; x++)
            {
                var c = src.Get(x, y);
                dst.At(x, y) = UIR.Cell.FromRaw(c.Rune, c.Fg, c.Bg, c.Flags, c.Width);
                // R1 steal: the directive table crosses with the cells — both
                // enums are byte-backed with identical members in identical
                // order, so the numeric cast is exact. Copied per cell (not
                // just when non-None): the snapshot buffer is retained across
                // calls and a stale Skip would hide the next frame.
                dst.SetDiffOption(x, y, (UIR.CellDiffOption)(byte)src.GetDiffOption(x, y), src.GetForcedWidth(x, y));
            }
        }

        dst.InvalidateAll();
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
        private readonly EngineCells.DiffEngine _engine;
        private readonly RowHashDiffEncoder _portable = new();
        private UIR.ScreenBuffer _snapshot = new(0, 0);

        public EngineLinkedEncoder(EngineCells.DiffEngine engine) =>
            _engine = engine ?? throw new ArgumentNullException(nameof(engine));

        /// <inheritdoc />
        public CellDiffBatch Encode(
            UIR.ScreenBuffer prev,
            UIR.ScreenBuffer next,
            IReadOnlyList<UIR.Rect>? hints,
            long sequence)
        {
            return _portable.Encode(prev, next, hints, sequence);
        }

        /// <summary>
        ///     Encodes the delta from the linked engine's front buffer (the last
        ///     frame the ANSI path flushed) to <paramref name="next"/>. The
        ///     snapshot buffer is retained across calls and only regrown, so
        ///     the steady-state cost is the copy itself, never an allocation.
        /// </summary>
        public CellDiffBatch EncodeCellForge(
            UIR.ScreenBuffer next,
            IReadOnlyList<UIR.Rect>? hints,
            long sequence)
        {
            ArgumentNullException.ThrowIfNull(next);
            SyncSnapshot();
            return _portable.Encode(_snapshot, next, hints, sequence);
        }

        private void SyncSnapshot()
        {
            var front = _engine.Front;
            if (_snapshot.Cols != front.Cols || _snapshot.Rows != front.Rows)
            {
                _snapshot = new UIR.ScreenBuffer(front.Cols, front.Rows);
            }

            CopyEngineToRendering(front, _snapshot);
        }
    }
}
