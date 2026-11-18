// #2361: reference leaf that moves data preparation out of Paint. On a cache
// miss it records ops into PrepaintContext.Scene once and commits the record;
// on a hit it skips OnPrepaint entirely. Paint only replays the prepared
// range, so it no longer reads state or builds cell data. BCL-only,
// render-thread owned, same leaf rule as the rest of the engine directory.

namespace Harbor.Tui.CellForge.Rendering;

/// <summary>
/// Leaf base for nodes whose paint output is prepared ahead of time. Subclasses
/// implement <see cref="Measure"/>, <see cref="OnPrepaint"/> and a version key;
/// <see cref="Paint"/> is sealed and replays whatever prepaint recorded.
/// The version key is the manual dependency signal. Entity reads recorded via
/// <see cref="PrepaintContext.MarkRead"/> are also captured on the cached
/// record: when the host's entity versions change, the context drops the
/// entry even though this key and the id are unchanged.
/// </summary>
public abstract class PrepaintNode : LeafNode, INode
{
    private PrepaintRecord _record;
    private Scene? _scene;
    private bool _prepainted;

    /// <summary>
    /// Version of the inputs that feed <see cref="OnPrepaint"/>. A changed value
    /// invalidates the cached record even when the element id is unchanged.
    /// The arranged bounds are folded into the key as well, so a resize forces
    /// a reprepare without the caller having to bump this version.
    /// </summary>
    protected abstract ulong PrepaintKey { get; }

    /// <summary>The record produced by the last prepaint pass.</summary>
    public PrepaintRecord CurrentRecord => _record;

    // The interface slot has a default body. Explicit implementation is what
    // makes a call through INode (the NodeFrame path) reach this class's
    // preparation instead of that default; the public overload below is the
    // same operation for callers holding the concrete node.
    PrepaintRecord INode.Prepaint(PrepaintContext context, ElementId id) => Prepaint(context, id);

    /// <summary>
    /// Prepaints this node: reuses a matching record, otherwise calls
    /// <see cref="OnPrepaint"/> and commits the result under <paramref name="id"/>.
    /// </summary>
    public PrepaintRecord Prepaint(PrepaintContext context, ElementId id)
    {
        ArgumentNullException.ThrowIfNull(context);
        _scene = context.Scene;

        // Bounds are part of the cache key exactly as in GPUI's
        // ViewElementCacheKey: recorded ops carry absolute coordinates, so a
        // moved or resized node must reprepare instead of replaying stale ops.
        ulong key = (ulong)(uint)HashCode.Combine(
            PrepaintKey.GetHashCode(),
            Arranged.X,
            Arranged.Y,
            Arranged.Width,
            Arranged.Height);
        if (context.TryReuse(id, key, out var cached))
        {
            _record = cached;
            _prepainted = true;
            return cached;
        }

        context.BeginReads();
        var prepared = OnPrepaint(context, id, Arranged);
        prepared = prepared with { ReadEntities = context.TakeReads() };
        _record = context.Commit(id, key, prepared);
        _prepainted = true;
        return _record;
    }

    /// <summary>Prepares bounds, style, accessed entities and a scene range.</summary>
    protected abstract PrepaintRecord OnPrepaint(PrepaintContext context, ElementId id, Rect bounds);

    /// <summary>Observer hook called before the prepared range is replayed.</summary>
    protected virtual void OnPaint(ScreenBuffer buffer, Rect area)
    {
    }

    /// <inheritdoc />
    public sealed override void Paint(ScreenBuffer buffer, Rect clip)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        if (!_prepainted || _scene is null)
        {
            return;
        }

        var area = _record.Bounds.Intersect(clip);
        if (area.Width <= 0 || area.Height <= 0 || _record.SceneRange.IsEmpty)
        {
            return;
        }

        OnPaint(buffer, area);
        _scene.ReplayTo(buffer, _record.SceneRange);
    }
}
