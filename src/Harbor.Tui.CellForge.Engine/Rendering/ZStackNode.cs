// TR1 (#1220, epic #1195): full-area overlap stack node.
// Same leaf rule as INode.cs: BCL-only, render-thread owned, chat untouched.

using System.Runtime.InteropServices;

namespace Harbor.Tui.CellForge.Rendering;

/// <summary>
/// Full-area overlap stack over <see cref="StackLayout.Arrange"/> — the layout
/// half of tab pages / stacked views / dialog overlays: every child takes the
/// whole rect, painted in index order (lower first). Painting order and
/// hit-testing stay with the consumer; this only hands out rects and forwards
/// the three phases.
/// <see cref="Add"/> / <see cref="Remove"/> are setup-time; the steady-state
/// frame path allocates nothing (<c>for</c> index loops, reused frame scratch
/// via <c>CollectionsMarshal.AsSpan</c>, no LINQ, no closures).
/// </summary>
public sealed class ZStackNode : ContainerNode
{
    private readonly List<INode> _children = [];
    private readonly List<Rect> _frames = [];

    /// <summary>Child count (z-order is index order, lower first).</summary>
    public int Count => _children.Count;

    /// <summary>Setup-time: pushes <paramref name="child"/> on top of the stack.</summary>
    public void Add(INode child)
    {
        ArgumentNullException.ThrowIfNull(child);
        _children.Add(child);
        _frames.Add(default);
    }

    /// <summary>Setup-time: removes <paramref name="child"/> from the stack.</summary>
    public bool Remove(INode child)
    {
        ArgumentNullException.ThrowIfNull(child);
        int index = _children.IndexOf(child);
        if (index < 0)
        {
            return false;
        }

        _children.RemoveAt(index);
        _frames.RemoveAt(index);
        return true;
    }

    /// <summary>
    /// Desired size: clamped max of the children's desired extents (every
    /// child takes the whole rect, so the stack wants whatever its largest
    /// child wants, bounded by <paramref name="constraints"/>).
    /// </summary>
    public override Size Measure(Constraints constraints)
    {
        int width = 0;
        int height = 0;
        for (int i = 0; i < _children.Count; i++)
        {
            Size want = _children[i].Measure(constraints);
            if (want.Width > width)
            {
                width = want.Width;
            }

            if (want.Height > height)
            {
                height = want.Height;
            }
        }

        return new Size(Math.Min(Math.Max(0, width), constraints.Width), Math.Min(Math.Max(0, height), constraints.Height));
    }

    /// <summary>
    /// Hands every child the whole rect through
    /// <see cref="StackLayout.Arrange"/> and forwards it. Zero-allocation.
    /// </summary>
    public override void Arrange(Rect rect)
    {
        SetArranged(rect);
        int n = _children.Count;
        if (n == 0)
        {
            return;
        }

        StackLayout.Arrange(Arranged, n, CollectionsMarshal.AsSpan(_frames));
        for (int i = 0; i < n; i++)
        {
            _children[i].Arrange(_frames[i]);
        }
    }

    /// <summary>Paints children bottom-to-top, each clipped to the stack ∩ clip.</summary>
    public override void Paint(ScreenBuffer buffer, Rect clip)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        for (int i = 0; i < _children.Count; i++)
        {
            Rect area = _frames[i].Intersect(clip);
            if (area.Width > 0 && area.Height > 0)
            {
                _children[i].Paint(buffer, area);
            }
        }
    }
}
