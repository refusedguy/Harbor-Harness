
namespace Harbor.Tui.CellForge.Rendering;

/// <summary>
///     Addressable subgraph invalidation on top of <see cref="DirtyRect"/>
///     (issue #2354, GPUI <c>dirty_views</c> / <c>WindowInvalidator</c> pattern).
///     Callers mark damage by view id or by explicit rect; the frame loop
///     registers each view's CURRENT frame, rebuilds a narrow
///     <c>DirtyRect</c> tree and emits only the regions that actually
///     intersect the damage. A single edited row therefore becomes a one-row
///     frame hint instead of a full-screen diff, while a broad mark keeps the
///     escape hatch (empty hints → fused full scan).
/// </summary>
/// <remarks>
///     Render-thread owned, like <see cref="DiffEngine"/> and
///     <see cref="DirtyRect"/>: not thread-safe. Callers marshal damage onto
///     the frame thread before <see cref="Drain"/>. Registration is
///     per-frame (geometry is solved every frame), so <see cref="ClearRegions"/>
///     + <see cref="Register"/> precede each drain. All hot operations are
///     allocation-free after setup except the small per-drain region table.
/// </remarks>
public sealed class ViewDamageTracker
{
    private readonly Dictionary<string, Rect> _regions = new(StringComparer.Ordinal);
    private readonly HashSet<string> _ids = new(StringComparer.Ordinal);
    private readonly List<Rect> _rects = [];
    private int _cols;
    private int _rows;
    private bool _broad;

    /// <summary>Number of registered view regions.</summary>
    public int RegionCount => _regions.Count;

    /// <summary>Pending id + rect marks not yet drained.</summary>
    public int PendingCount => _ids.Count + _rects.Count;

    /// <summary>True when a broad mark is pending (full-scan escape hatch).</summary>
    public bool IsBroadPending => _broad;

    /// <summary>Sets the screen viewport the region frames are expressed in.</summary>
    public void SetViewport(int cols, int rows)
    {
        _cols = Math.Max(0, cols);
        _rows = Math.Max(0, rows);
    }

    /// <summary>
    ///     Registers (or retargets) a view region. <paramref name="frame"/> is
    ///     in screen coordinates; an empty frame is ignored (hidden view).
    /// </summary>
    public void Register(string id, Rect frame)
    {
        if (string.IsNullOrEmpty(id) || frame.Width <= 0 || frame.Height <= 0)
        {
            return;
        }

        _regions[id] = frame;
    }

    /// <summary>Drops all registered regions (call before re-registering a frame).</summary>
    public void ClearRegions() => _regions.Clear();

    /// <summary>Marks one view subgraph dirty.</summary>
    public void Damage(string id)
    {
        if (!string.IsNullOrEmpty(id))
        {
            _ids.Add(id);
        }
    }

    /// <summary>Marks several view subgraphs dirty.</summary>
    public void Damage(params string[] ids)
    {
        if (ids is null)
        {
            return;
        }

        for (int i = 0; i < ids.Length; i++)
        {
            Damage(ids[i]);
        }
    }

    /// <summary>Marks an arbitrary screen rect dirty (addressable by rect).</summary>
    public void Damage(in Rect rect)
    {
        if (rect.Width > 0 && rect.Height > 0)
        {
            _rects.Add(rect);
        }
    }

    /// <summary>Marks the whole viewport dirty (the no-hints full-scan path).</summary>
    public void DamageAll() => _broad = true;

    /// <summary>
    ///     Replaces a broad (or rect) mark with one precise view id. Used when
    ///     the mutation turns out to touch a single subgraph (e.g. a plain
    ///     composer edit), so the conservative input-time mark can be narrowed.
    /// </summary>
    public void NarrowTo(string id)
    {
        _broad = false;
        _ids.Clear();
        _rects.Clear();
        Damage(id);
    }

    /// <summary>Drops every pending mark without emitting hints (e.g. a broad frame).</summary>
    public void Reset()
    {
        _broad = false;
        _ids.Clear();
        _rects.Clear();
    }

    /// <summary>
    ///     Resolves pending damage into narrow, screen-space hint rects and
    ///     clears the pending marks. Returns <see langword="true"/> when the
    ///     caller must take the full-scan path (broad mark or no viewport);
    ///     in that case <paramref name="count"/> is zero. Overlapping or
    ///     adjacent regions are merged conservatively. Hints beyond
    ///     <paramref name="hints"/>.Length are dropped, so supply at least
    ///     <c>RegionCount + 1</c> slots.
    /// </summary>
    public bool Drain(Span<Rect> hints, out int count)
    {
        count = 0;
        bool broad = _broad;
        _broad = false;
        if (broad || _cols <= 0 || _rows <= 0)
        {
            Reset();
            return true;
        }

        if (_ids.Count == 0 && _rects.Count == 0)
        {
            return false;
        }

        var screen = new Rect(0, 0, _cols, _rows);
        var root = new DirtyRect(_cols, _rows);
        var children = new Dictionary<string, (DirtyRect Node, Rect Frame)>(StringComparer.Ordinal);
        foreach (var pair in _regions)
        {
            var frame = pair.Value.Intersect(screen);
            if (frame.Width <= 0 || frame.Height <= 0)
            {
                continue;
            }

            var child = new DirtyRect(frame.Width, frame.Height);
            root.AddChild(child, frame);
            children.Add(pair.Key, (child, frame));
        }

        foreach (var id in _ids)
        {
            if (children.TryGetValue(id, out var entry))
            {
                entry.Node.Invalidate(new Rect(0, 0, entry.Node.Width, entry.Node.Height));
            }
        }

        foreach (var rect in _rects)
        {
            root.Invalidate(rect.Intersect(screen));
        }

        foreach (var pair in children)
        {
            if (pair.Value.Node.Dirty is { } local)
            {
                var frame = pair.Value.Frame;
                AddHint(new Rect(frame.X + local.X, frame.Y + local.Y, local.Width, local.Height), hints, ref count);
            }
        }

        // Damage outside any registered region stays on the root and must be
        // hinted too (the narrow cascade cannot reach it through a child).
        if (root.Dirty is { } rest)
        {
            AddHint(rest, hints, ref count);
        }

        Reset();
        return false;
    }

    private static void AddHint(Rect rect, Span<Rect> hints, ref int count)
    {
        if (rect.Width <= 0 || rect.Height <= 0 || count >= hints.Length)
        {
            return;
        }

        for (int i = 0; i < count; i++)
        {
            if (hints[i].Intersect(rect) == default)
            {
                continue;
            }

            hints[i] = Union(hints[i], rect);
            return;
        }

        hints[count++] = rect;
    }

    private static Rect Union(Rect a, Rect b)
    {
        int left = Math.Min(a.X, b.X);
        int top = Math.Min(a.Y, b.Y);
        int right = Math.Max(a.Right, b.Right);
        int bottom = Math.Max(a.Bottom, b.Bottom);
        return new Rect(left, top, right - left, bottom - top);
    }
}
