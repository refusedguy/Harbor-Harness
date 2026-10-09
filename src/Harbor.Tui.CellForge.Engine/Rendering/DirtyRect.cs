
namespace Harbor.Tui.CellForge.Rendering;

/// <summary>
/// Viewport-local dirty-rect tracker (ENG2, Terminal.Gui <c>NeedsDraw</c> lesson).
/// Each node owns a single dirty rect in its own viewport coordinates plus a
/// child subtree with frames expressed in the parent's coordinates:
/// <list type="bullet">
///   <item><description><c>Invalidate</c> clips to the viewport and merges via
///     <c>Union</c> (conservative: the bounding rect may cover cells neither
///     input damaged, never fewer — the same contract as
///     <c>DiffEngine.FrameHint</c>);</description></item>
///   <item><description>invalidation cascades NARROW: each child receives only
///     the intersection with its frame, translated into child-local
///     coordinates — viewports outside the region stay clean, so a spinner
///     tick never degrades into a full-screen invalidation;</description></item>
///   <item><description>self vs children drawing gate separately:
///     <c>NeedsDraw</c> (own viewport dirty) and <c>ChildNeedsDraw</c> (any
///     descendant dirty) let the paint loop skip untouched levels;</description></item>
///   <item><description>clears are narrow too: <c>ClearDrawn</c> acknowledges
///     only the painted region (partial cover keeps the remainder dirty —
///     damage is never dropped silently) and cascades the translated
///     intersection down.</description></item>
/// </list>
/// Render-thread owned, not thread-safe (same as <c>DiffEngine</c>).
/// All operations are allocation-free after setup (<c>for</c> index loops,
/// no LINQ, no closures); <c>AddChild</c>/<c>RemoveChild</c> are setup-time.
/// </summary>
public sealed class DirtyRect
{
    private readonly List<ChildEntry> _children = [];
    private Rect? _dirty;
    private bool _childDirty;
    private DirtyRect? _parent;

    public DirtyRect(int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(width);
        ArgumentOutOfRangeException.ThrowIfNegative(height);
        Width = width;
        Height = height;
    }

    /// <summary>Viewport width this node tracks.</summary>
    public int Width { get; }

    /// <summary>Viewport height this node tracks.</summary>
    public int Height { get; }

    /// <summary>Viewport-local dirty union (<c>null</c> = clean).</summary>
    public Rect? Dirty => _dirty;

    /// <summary>True when this viewport itself needs repainting.</summary>
    public bool NeedsDraw => _dirty.HasValue;

    /// <summary>True when any descendant needs repainting (self excluded).</summary>
    public bool ChildNeedsDraw => _childDirty;

    /// <summary>True when this node or any descendant needs repainting.</summary>
    public bool NeedsAnyDraw => _dirty.HasValue || _childDirty;

    /// <summary>Attached children (setup-time topology).</summary>
    public int ChildCount => _children.Count;

    /// <summary>Dirty cell count in local coordinates (0 when clean).</summary>
    public long DirtyArea => _dirty?.Area ?? 0;

    /// <summary>
    /// Explicit full-screen requests via <see cref="InvalidateAll"/> on this
    /// node (cascade fills are not counted — the moat pins the narrow path
    /// never needs the escape hatch).
    /// </summary>
    public int FullInvalidations { get; private set; }

    /// <summary>
    /// Attaches a child whose <paramref name="frame"/> is expressed in this
    /// node's coordinates. A child has exactly one parent; attaching a dirty
    /// child marks the new ancestor chain.
    /// </summary>
    public void AddChild(DirtyRect child, Rect frame)
    {
        ArgumentNullException.ThrowIfNull(child);
        if (child._parent is not null)
        {
            throw new InvalidOperationException("Child is already attached to a DirtyRect.");
        }

        child._parent = this;
        _children.Add(new ChildEntry(child, frame));
        if (child.NeedsAnyDraw)
        {
            MarkChildDirty();
        }
    }

    /// <summary>
    /// Detaches a child (it keeps its own dirty state as an independent
    /// root) and refreshes the ancestor flags. Returns false when absent.
    /// </summary>
    public bool RemoveChild(DirtyRect child)
    {
        ArgumentNullException.ThrowIfNull(child);
        for (int i = 0; i < _children.Count; i++)
        {
            if (ReferenceEquals(_children[i].Node, child))
            {
                _children.RemoveAt(i);
                child._parent = null;
                RefreshChildFlagUpwards();
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Marks a region damaged: clipped to the viewport, unioned into the
    /// local dirty rect, bubbled up the ancestor chain, and cascaded narrow
    /// (frame intersection, child-local translation) into children.
    /// Empty or fully off-screen rects are no-ops.
    /// </summary>
    public void Invalidate(in Rect rect)
    {
        var clipped = rect.Intersect(new Rect(0, 0, Width, Height));
        if (clipped.Width <= 0 || clipped.Height <= 0)
        {
            return;
        }

        _dirty = _dirty.HasValue ? Union(_dirty.Value, clipped) : clipped;
        BubbleSubtreeDirty();
        CascadeToChildren(clipped);
    }

    /// <summary>
    /// Full-screen escape hatch (resize, theme swap, untrackable damage):
    /// dirties the whole viewport and cascades the translated intersections
    /// down. Prefer <see cref="Invalidate"/> — every call is counted in
    /// <see cref="FullInvalidations"/>.
    /// </summary>
    public void InvalidateAll()
    {
        if (Width <= 0 || Height <= 0)
        {
            return;
        }

        FullInvalidations++;
        var full = new Rect(0, 0, Width, Height);
        _dirty = full;
        BubbleSubtreeDirty();
        CascadeToChildren(full);
    }

    /// <summary>
    /// Acknowledges a painted region (in this node's coordinates): the local
    /// dirty rect clears only when fully covered — partial cover keeps the
    /// remainder dirty (conservative, never drops damage) — and the
    /// translated intersection cascades narrow into children. Ancestor flags
    /// refresh on the way up.
    /// </summary>
    public void ClearDrawn(in Rect painted)
    {
        if (_dirty.HasValue && Contains(painted, _dirty.Value))
        {
            _dirty = null;
        }

        var children = _children;
        for (int i = 0; i < children.Count; i++)
        {
            var frame = children[i].Frame;
            var hit = painted.Intersect(frame);
            if (hit.Width <= 0 || hit.Height <= 0)
            {
                continue;
            }

            children[i].Node.ClearDrawn(new Rect(
                hit.X - frame.X, hit.Y - frame.Y, hit.Width, hit.Height));
        }

        RefreshChildFlagUpwards();
    }

    /// <summary>Recursive full clear (e.g. after a full repaint).</summary>
    public void ClearAll()
    {
        _dirty = null;
        var children = _children;
        for (int i = 0; i < children.Count; i++)
        {
            children[i].Node.ClearAll();
        }

        RefreshChildFlagUpwards();
    }

    private void CascadeToChildren(Rect clipped)
    {
        var children = _children;
        for (int i = 0; i < children.Count; i++)
        {
            var frame = children[i].Frame;
            var hit = clipped.Intersect(frame);
            if (hit.Width <= 0 || hit.Height <= 0)
            {
                continue;
            }

            children[i].Node.Invalidate(new Rect(
                hit.X - frame.X, hit.Y - frame.Y, hit.Width, hit.Height));
        }
    }

    private void BubbleSubtreeDirty()
    {
        var ancestor = _parent;
        while (ancestor is not null)
        {
            if (ancestor._childDirty)
            {
                break;
            }

            ancestor._childDirty = true;
            ancestor = ancestor._parent;
        }
    }

    private void MarkChildDirty()
    {
        _childDirty = true;
        BubbleSubtreeDirty();
    }

    private void RefreshChildFlagUpwards()
    {
        _childDirty = HasDirtyDescendant();
        var ancestor = _parent;
        while (ancestor is not null)
        {
            bool any = ancestor.HasDirtyDescendant();
            if (any == ancestor._childDirty)
            {
                break;
            }

            ancestor._childDirty = any;
            ancestor = ancestor._parent;
        }
    }

    private bool HasDirtyDescendant()
    {
        var children = _children;
        for (int i = 0; i < children.Count; i++)
        {
            if (children[i].Node.NeedsAnyDraw)
            {
                return true;
            }
        }

        return false;
    }

    private static bool Contains(Rect outer, Rect inner) =>
        outer.X <= inner.X
        && outer.Y <= inner.Y
        && outer.Right >= inner.Right
        && outer.Bottom >= inner.Bottom;

    /// <summary>
    /// Smallest rect covering both inputs (hint-union merge). Mirrors
    /// <c>DiffEngine.Union</c>; kept local so the diff file stays untouched
    /// (ENG2 scope is invalidation only).
    /// </summary>
    private static Rect Union(Rect a, Rect b)
    {
        int left = Math.Min(a.X, b.X);
        int top = Math.Min(a.Y, b.Y);
        int right = Math.Max(a.Right, b.Right);
        int bottom = Math.Max(a.Bottom, b.Bottom);
        return new Rect(left, top, right - left, bottom - top);
    }

    private readonly record struct ChildEntry(DirtyRect Node, Rect Frame);
}
