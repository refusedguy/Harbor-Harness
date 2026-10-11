// TR1 (#1220, epic #1195): container base + n-ary flex split node.
// Same leaf rule as INode.cs: BCL-only, render-thread owned, chat untouched.

using System.Runtime.InteropServices;

namespace Harbor.Tui.CellForge.Rendering;

/// <summary>
/// Node with children. Owns the <see cref="INode.Arranged"/> plumbing;
/// child ownership lives in subclasses (<see cref="FlexSplitNode"/> keeps a
/// track per child, <see cref="ZStackNode"/> a plain ordered list), so this
/// base carries no list and no policy.
/// </summary>
public abstract class ContainerNode : INode
{
    /// <inheritdoc />
    public Rect Arranged { get; private set; }

    /// <summary>Stores the normalized rect (negative extents count as zero).</summary>
    protected void SetArranged(Rect rect) =>
        Arranged = new Rect(rect.X, rect.Y, Math.Max(0, rect.Width), Math.Max(0, rect.Height));

    /// <inheritdoc />
    public abstract Size Measure(Constraints constraints);

    /// <inheritdoc />
    public abstract void Arrange(Rect rect);

    /// <inheritdoc />
    public abstract void Paint(ScreenBuffer buffer, Rect clip);
}

/// <summary>
/// N-ary Row (<see cref="SplitDir.Horizontal"/>) / Column
/// (<see cref="SplitDir.Vertical"/>) over <see cref="FlexLayout.Arrange"/> —
/// the generic-composition seat of the PRIM8 #303 flex solver. One
/// <see cref="FlexTrack"/> per child; gap and justify behave exactly as in
/// <see cref="FlexLayout"/>.
/// <see cref="Add(INode, FlexTrack)"/> / <see cref="Remove"/> are setup-time;
/// the steady-state frame path (<see cref="Measure"/>, <see cref="Arrange"/>,
/// <see cref="Paint"/>) allocates nothing (<c>for</c> index loops, reused
/// frame scratch via <c>CollectionsMarshal.AsSpan</c>, no LINQ, no closures).
/// </summary>
public sealed class FlexSplitNode : ContainerNode
{
    private readonly List<INode> _children = [];
    private readonly List<FlexTrack> _tracks = [];
    private readonly List<Rect> _frames = [];

    /// <summary>Creates a split node with the given axis, gap and justification.</summary>
    /// <param name="direction">Main axis: Row vs Column.</param>
    /// <param name="gap">Base cells between adjacent children.</param>
    /// <param name="justify">Placement of leftover space on underfill.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="gap"/> is negative.</exception>
    public FlexSplitNode(SplitDir direction, int gap = 0, FlexJustify justify = FlexJustify.Start)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(gap);
        Direction = direction;
        Gap = gap;
        Justify = justify;
    }

    /// <summary>Main axis.</summary>
    public SplitDir Direction { get; }

    /// <summary>Base cells between adjacent children.</summary>
    public int Gap { get; }

    /// <summary>Placement of leftover space on underfill.</summary>
    public FlexJustify Justify { get; }

    /// <summary>Child count.</summary>
    public int Count => _children.Count;

    /// <summary>Setup-time: appends <paramref name="child"/> with its track.</summary>
    public void Add(INode child, FlexTrack track)
    {
        ArgumentNullException.ThrowIfNull(child);
        _children.Add(child);
        _tracks.Add(track);
        _frames.Add(default);
    }

    /// <summary>Setup-time: removes <paramref name="child"/> and its track/frame.</summary>
    public bool Remove(INode child)
    {
        ArgumentNullException.ThrowIfNull(child);
        int index = _children.IndexOf(child);
        if (index < 0)
        {
            return false;
        }

        _children.RemoveAt(index);
        _tracks.RemoveAt(index);
        _frames.RemoveAt(index);
        return true;
    }

    /// <summary>
    /// Desired size: fixed/percent shares plus <c>Min</c> floors summed along
    /// the main axis (flexible tracks otherwise want zero — they absorb the
    /// remainder at arrange time), gaps included, clamped to the bounds;
    /// cross axis takes the clamped max of the children's desired extents.
    /// </summary>
    public override Size Measure(Constraints constraints)
    {
        bool horizontal = Direction == SplitDir.Horizontal;
        int mainMax = horizontal ? constraints.Width : constraints.Height;
        int crossMax = horizontal ? constraints.Height : constraints.Width;

        int main = 0;
        int cross = 0;
        for (int i = 0; i < _children.Count; i++)
        {
            var track = _tracks[i];
            main += track.IsFlex
                ? track.FlexFloor
                : track.IsPercent
                    ? (int)(mainMax * track.Fraction)
                    : track.FixedCells;

            Size want = _children[i].Measure(constraints);
            int childCross = horizontal
                ? Math.Max(0, want.Height)
                : Math.Max(0, want.Width);
            if (childCross > cross)
            {
                cross = childCross;
            }
        }

        if (_children.Count > 1)
        {
            main += Gap * (_children.Count - 1);
        }

        main = Math.Min(main, mainMax);
        cross = Math.Min(cross, crossMax);
        return horizontal ? new Size(main, cross) : new Size(cross, main);
    }

    /// <summary>
    /// Deals one frame per child through <see cref="FlexLayout.Arrange"/> and
    /// forwards it. Zero-allocation: the frame scratch is reused across frames.
    /// </summary>
    public override void Arrange(Rect rect)
    {
        SetArranged(rect);
        int n = _children.Count;
        if (n == 0)
        {
            return;
        }

        FlexLayout.Arrange(
            Direction,
            Arranged,
            CollectionsMarshal.AsSpan(_tracks),
            CollectionsMarshal.AsSpan(_frames),
            Gap,
            Justify);
        for (int i = 0; i < n; i++)
        {
            _children[i].Arrange(_frames[i]);
        }
    }

    /// <summary>Paints children in order, each clipped to its frame ∩ clip.</summary>
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
