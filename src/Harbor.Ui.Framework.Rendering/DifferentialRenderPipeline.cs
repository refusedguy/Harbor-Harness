namespace Harbor.Ui.Framework.Rendering.Protocol;

using System.Collections.Immutable;

/// <summary>
///     The differential render pipeline (renderer-unification sprint Phase
///     6.2): a producer-side frame differ that emits portable
///     <see cref="CellDiffBatch"/>es to subscribed sinks, and a consumer-side
///     applier that replays batches onto a <see cref="ScreenBuffer"/>.
/// </summary>
/// <remarks>
///     <para>
///         Producer mode: keep the pipeline, call <see cref="Render"/> with
///         each new frame. The pipeline owns the front (already-shown) buffer
///         and advances it to the given frame on every call.
///     </para>
///     <para>
///         Consumer mode: backends that render through their own widget stack
///         (SpectreTui panels, Avalonia/Blazor surfaces) implement
///         <see cref="ICellDiffSink"/> and translate the batch into native
///         invalidation — or replay it onto a private <see cref="ScreenBuffer"/>
///         via <see cref="ApplyTo"/>. Both sides are version-agnostic: V1
///         batches (no hints) and V2 batches (with hints) apply identically.
///     </para>
/// </remarks>
public sealed class DifferentialRenderPipeline
{
    private readonly ICellDiffEncoder _encoder;
    private readonly List<ICellDiffSink> _sinks = [];
    private ScreenBuffer? _front;
    private long _sequence;

    public DifferentialRenderPipeline(ICellDiffEncoder? encoder = null)
    {
        _encoder = encoder ?? new RowHashDiffEncoder();
    }

    /// <summary>Registered sinks; add backends with <see cref="Subscribe"/>.</summary>
    public IReadOnlyList<ICellDiffSink> Sinks => _sinks;

    /// <summary>Sequence number of the most recently emitted batch.</summary>
    public long Sequence => _sequence;

    /// <summary>Subscribes a backend to the pipeline.</summary>
    public void Subscribe(ICellDiffSink sink) => _sinks.Add(sink);

    /// <summary>
    ///     Differences the given frame against the previous one, publishes the
    ///     batch to every subscribed sink, and returns it. The first call
    ///     after construction (or after <see cref="Reset"/>) is a full
    ///     repaint: every differing cell is reported.
    /// </summary>
    /// <param name="next">The composed current frame.</param>
    /// <param name="hints">Optional damage rects narrowing the producer scan.</param>
    public CellDiffBatch Render(ScreenBuffer next, IReadOnlyList<Rect>? hints = null)
    {
        if (_front is null || _front.Cols != next.Cols || _front.Rows != next.Rows)
        {
            _front = new ScreenBuffer(next.Cols, next.Rows);
        }

        long sequence = ++_sequence;
        CellDiffBatch batch = _encoder.Encode(_front, next, hints, sequence);

        _front = CloneBackToFront(next);
        for (int i = 0; i < _sinks.Count; i++)
        {
            _sinks[i].Accept(batch);
        }

        return batch;
    }

    /// <summary>Forgets the shown frame; the next <see cref="Render"/> is a full repaint.</summary>
    public void Reset()
    {
        _front = null;
        _sequence = 0;
    }

    /// <summary>
    ///     Consumer-side replay: applies <paramref name="batch"/> to
    ///     <paramref name="target"/>. Works for every protocol version this
    ///     assembly supports (V1 and V2 — hints are advisory only).
    /// </summary>
    public static void ApplyTo(in CellDiffBatch batch, ScreenBuffer target)
    {
        ImmutableArray<CellDiffMessage> changes = batch.Changes;
        for (int i = 0; i < changes.Length; i++)
        {
            CellDiffMessage m = changes[i];
            if (m.X < 0 || m.Y < 0 || m.X >= batch.Cols || m.Y >= batch.Rows)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(batch), $"Cell change ({m.X},{m.Y}) outside {batch.Cols}x{batch.Rows} frame.");
            }

            target.At(m.X, m.Y) = m.NewCell;
            target.MarkRowDirty(m.Y);
        }
    }

    private ScreenBuffer CloneBackToFront(ScreenBuffer next)
    {
        // The front buffer must become an exact copy of `next`. Cheapest
        // correct path: hash-verified cell copy via SetCell per differing
        // cell is O(changes); a wholesale rebuild is O(cells) once per frame.
        // Use SetCell for every cell of changed rows (hash-driven).
        ScreenBuffer front = _front!;
        for (int y = 0; y < next.Rows; y++)
        {
            if (front.RowHashCode(y) != next.RowHashCode(y))
            {
                for (int x = 0; x < next.Cols; x++)
                {
                    front.At(x, y) = next.Get(x, y);
                }

                front.MarkRowDirty(y);
            }
        }

        return front;
    }
}
