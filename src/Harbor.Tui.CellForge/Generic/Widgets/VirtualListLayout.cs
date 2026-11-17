using Harbor.Tui.CellForge.Widgets;
using Harbor.Ui.Framework.Rendering;

namespace Harbor.Tui.CellForge.Generic.Widgets;

/// <summary>
/// Domain-neutral virtualization core driven by an <see cref="IVirtualListDelegate"/>.
///
/// It owns only geometry: a monotonic prefix of item tops, a cheap estimate
/// for every index and an exact measurement for the items that reached the
/// viewport. <see cref="Count"/>, <see cref="VirtualItemMeasure"/> and
/// <see cref="IVirtualListDelegate.Render"/> all come from the delegate, so the
/// same window serves a chat feed, a file list, a log view or a table without
/// a domain type in the layout engine. The visible-range math mirrors
/// <c>TimelineLayoutCache</c> (binary search + suffix scan); arrays grow
/// geometrically and are reused, so a steady-state frame allocates nothing.
/// </summary>
public sealed class VirtualListLayout
{
    private long[] _virtual = [0]; // _virtual[i] = top row of item i; [_count] = total height
    private int[] _heights = [];   // effective height: estimate until measured, then exact
    private bool[] _measured = [];
    private int _count;
    private int _width = -1;
    private int _unmeasuredFrom;
    private IVirtualListDelegate? _delegate;

    /// <summary>Current item count, read from the attached delegate.</summary>
    public int Count => _count;

    /// <summary>Settled virtual height of the whole list.</summary>
    public long TotalHeight => _virtual[_count];

    /// <summary>Virtual top row of item <paramref name="index"/>.</summary>
    public long ItemTop(int index) => _virtual[index];

    /// <summary>Effective height currently cached for item <paramref name="index"/>.</summary>
    public int ItemHeight(int index) => _heights[index];

    /// <summary>True once the item has been measured at the active width.</summary>
    public bool IsMeasured(int index) => _measured[index];

    /// <summary>
    /// Binds a delegate. Rebinding to a different source resets all
    /// measurements; rebinding to the same source keeps them, so append-only
    /// frames stay O(changed suffix).
    /// </summary>
    public void Attach(IVirtualListDelegate del)
    {
        ArgumentNullException.ThrowIfNull(del);
        if (!ReferenceEquals(_delegate, del))
        {
            _delegate = del;
            _width = -1;
            _unmeasuredFrom = 0;
        }

        SyncCount(del.Count);
    }

    /// <summary>
    /// Refreshes estimates and measures the items intersecting the viewport.
    /// Must be called after <see cref="Attach"/> (or after a count/width change).
    /// Returns true when a height changed and the virtual prefix was patched.
    /// </summary>
    public bool Prepare(int width, int viewportH, long scrollY)
    {
        var del = _delegate ?? throw new InvalidOperationException("No delegate attached.");
        ArgumentOutOfRangeException.ThrowIfNegative(width);
        ArgumentOutOfRangeException.ThrowIfNegative(viewportH);

        SyncCount(del.Count);

        if (_width != width)
        {
            // A height measured at another width is not authoritative here.
            Array.Clear(_measured, 0, _count);
            Array.Clear(_heights, 0, _count);
            _width = width;
            _unmeasuredFrom = 0;
        }

        int patchFrom = int.MaxValue;

        for (int i = _unmeasuredFrom; i < _count; i++)
        {
            if (!_measured[i] && _heights[i] == 0)
            {
                _heights[i] = Math.Max(1, del.EstimateHeight(i, width));
                patchFrom = Math.Min(patchFrom, i);
            }
        }

        _unmeasuredFrom = _count;

        if (_count > 0 && viewportH > 0)
        {
            var (first, last) = VisibleRange(scrollY, viewportH);
            for (int i = first; i <= last && i < _count; i++)
            {
                if (_measured[i])
                {
                    continue;
                }

                var m = del.Measure(i, width);
                _heights[i] = m.BestGuess;
                _measured[i] = true;
                patchFrom = Math.Min(patchFrom, i);
            }
        }

        if (patchFrom == int.MaxValue)
        {
            return false;
        }

        PatchVirtualFrom(patchFrom);
        return true;
    }

    /// <summary>Inclusive indices of items intersecting [scrollY, scrollY + viewportH).</summary>
    public (int First, int Last) VisibleRange(long scrollY, int viewportH)
    {
        if (_count == 0 || viewportH <= 0)
        {
            return (-1, -2);
        }

        int first = EntryAtY(Math.Max(0, scrollY));
        int last = first;
        while (last < _count && _virtual[last] < scrollY + viewportH)
        {
            last++;
        }

        return (first, last - 1);
    }

    /// <summary>Index of the item whose span contains row <paramref name="y"/>.</summary>
    public int EntryAtY(long y)
    {
        if (_count == 0)
        {
            return -1;
        }

        int lo = 0, hi = _count - 1;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) >> 1;
            if (_virtual[mid] <= y)
            {
                lo = mid;
            }
            else
            {
                hi = mid - 1;
            }
        }

        return lo;
    }

    /// <summary>Largest legal scroll offset for a viewport of <paramref name="viewportH"/> rows.</summary>
    public long MaxScrollFor(int viewportH) => ScrollableViewport.MaxOffsetFor(TotalHeight, viewportH);

    /// <summary>Clamps a scroll offset into the settled virtual range.</summary>
    public long ClampScroll(long scrollY, int viewportH) =>
        ScrollableViewport.ClampOffsetFor(scrollY, TotalHeight, viewportH);

    /// <summary>
    /// Paints only the visible window through the delegate. The rect is
    /// blanked first so vacated rows never keep stale cells during a scroll.
    /// </summary>
    public void Paint(ScreenBuffer buffer, Rect rect, long scrollY, long tick)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        var del = _delegate ?? throw new InvalidOperationException("No delegate attached.");
        if (_count == 0 || rect.Width <= 0 || rect.Height <= 0)
        {
            return;
        }

        buffer.Fill(rect, Cell.Blank);

        long top = ClampScroll(scrollY, rect.Height);
        var (first, last) = VisibleRange(top, rect.Height);
        for (int i = first; i <= last && i < _count; i++)
        {
            long relTop = _virtual[i] - top;
            int screenY = rect.Y + (int)Math.Max(0, relTop);
            int skipRows = relTop < 0 ? -(int)relTop : 0;
            int clipped = Math.Min(_heights[i] - skipRows, rect.Bottom - screenY);
            if (clipped <= 0)
            {
                continue;
            }

            del.Render(i, new VirtualItemPaintContext(
                buffer, new Rect(rect.X, screenY, rect.Width, clipped), tick, skipRows));
        }
    }

    private void SyncCount(int count)
    {
        int old = _count;
        if (count == old)
        {
            return;
        }

        if (count > _heights.Length)
        {
            int cap = Math.Max(Math.Max(4, _heights.Length * 2), count);
            Array.Resize(ref _heights, cap);
            Array.Resize(ref _measured, cap);
        }

        for (int i = old; i < count; i++)
        {
            _measured[i] = false;
            _heights[i] = 0;
        }

        _count = count;
        EnsureVirtualLength(count + 1);
        _unmeasuredFrom = Math.Min(_unmeasuredFrom, old);
        PatchVirtualFrom(0);
    }

    private void EnsureVirtualLength(int needed)
    {
        if (_virtual.Length < needed)
        {
            Array.Resize(ref _virtual, Math.Max(Math.Max(4, _virtual.Length * 2), needed));
        }
    }

    private void PatchVirtualFrom(int from)
    {
        if (from >= _count)
        {
            _virtual[_count] = _count > 0 ? _virtual[_count - 1] + _heights[_count - 1] : 0;
            return;
        }

        long sum = from == 0 ? 0 : _virtual[from - 1] + _heights[from - 1];
        for (int i = from; i < _count; i++)
        {
            _virtual[i] = sum;
            sum += _heights[i];
        }

        _virtual[_count] = sum;
    }
}
