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

    // Explicit slot implementation: a call through INode otherwise resolves to
    // the interface's default body instead of this virtual method.
    PrepaintRecord INode.Prepaint(PrepaintContext context, ElementId id) => Prepaint(context, id);

    /// <summary>
    /// Ordered children addressed by index. Overridden by containers whose
    /// child ids are positional; named-id containers override
    /// <see cref="Prepaint"/> instead.
    /// </summary>
    protected virtual IReadOnlyList<INode> ChildNodes => Array.Empty<INode>();

    /// <summary>
    /// Default prepaint: forward to <see cref="ChildNodes"/> with indexed ids.
    /// Keeping this here means a positional container only declares its child
    /// list, and the id rule lives in one place.
    /// </summary>
    public virtual PrepaintRecord Prepaint(PrepaintContext context, ElementId id)
    {
        PrepaintForward.Indexed(context, id, ChildNodes);
        return PrepaintRecord.Empty;
    }
}

/// <summary>
/// N-ary Row (<see cref="SplitDir.Horizontal"/>) / Column
/// (<see cref="SplitDir.Vertical"/>) over <see cref="FlexLayout.Arrange"/> —
/// the generic-composition seat of the PRIM8 #303 flex solver. One
/// <see cref="FlexTrack"/> per child
/// (<c>Fixed</c> / <c>Percent</c> / <c>Fill</c> / <c>Min</c> / <c>Max</c> /
/// <c>Auto</c>); gap and justify behave exactly as in
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

    // #2521: one cached solve per node. The key is the normalized Rect plus the
    // track list fingerprint (recomputed only on the setup-time Add/Remove path)
    // and, when Auto tracks are present, their per-frame intrinsic fingerprint.
    // All fields are value types; the steady-state frame path allocates nothing.
    private bool _hasLayout;
    private Rect _layoutRect;
    private ulong _layoutTracks;
    private ulong _layoutIntrinsic;
    private ulong _trackSignature;
    private bool _hasAuto;
    private int _hits;
    private int _misses;

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
        RefreshTrackSignature();
    }

    /// <summary>Main axis.</summary>
    public SplitDir Direction { get; }

    /// <summary>Base cells between adjacent children.</summary>
    public int Gap { get; }

    /// <summary>Placement of leftover space on underfill.</summary>
    public FlexJustify Justify { get; }

    /// <summary>Child count.</summary>
    public int Count => _children.Count;

    /// <summary>
    /// Solves skipped because the key matched the cached one. Test/diagnostic
    /// counter (#2521): a repeated <see cref="Arrange"/> with the same Rect and
    /// tracks increments this without re-running the solver.
    /// </summary>
    public int Hits => _hits;

    /// <summary>Solver runs since construction. Test/diagnostic counter (#2521).</summary>
    public int Misses => _misses;

    /// <inheritdoc />
    protected override IReadOnlyList<INode> ChildNodes => _children;

    /// <summary>Setup-time: appends <paramref name="child"/> with its track.</summary>
    public void Add(INode child, FlexTrack track)
    {
        ArgumentNullException.ThrowIfNull(child);
        _children.Add(child);
        _tracks.Add(track);
        _frames.Add(default);
        RefreshTrackSignature();
    }

    /// <summary>
    /// Setup-time: appends <paramref name="child"/> seated from the cell
    /// Measure/Render contract (#1209, epic #1195): the
    /// <paramref name="measure"/> + <paramref name="expand"/> pair maps
    /// through <see cref="WidgetMeasure.ToTrack"/> (expand absorbs the
    /// remainder with the measured floor; fixed takes the measured fit).
    /// </summary>
    public void Add(INode child, Measurement measure, bool expand) =>
        Add(child, WidgetMeasure.ToTrack(measure, expand));

    /// <summary>
    /// Setup-time: seats a <see cref="MeasuredLeaf"/> as an <see cref="FlexTrack.Auto"/>
    /// track whose floor/cap come from its min/max-content proposal (#2360).
    /// The leaf stays the single source of its intrinsic size and the existing
    /// <see cref="FlexLayout"/> solver stays the single owner of distribution —
    /// no parallel measure pass. On arrange, the leaf's definite
    /// <see cref="MeasuredLeaf.Measure"/> still supplies the intrinsic value
    /// the two-pass solver clamps into that floor/cap.
    /// </summary>
    public void AddMeasured(MeasuredLeaf leaf)
    {
        ArgumentNullException.ThrowIfNull(leaf);
        Add(leaf, leaf.ToTrack(Direction));
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
        RefreshTrackSignature();
        return true;
    }

    /// <summary>
    /// Desired size: fixed/percent/ratio shares, <c>Auto</c> intrinsics (the child's
    /// own desired main extent clamped to the track bounds) plus <c>Min</c>
    /// floors summed along the main axis (other flexible tracks want zero —
    /// they absorb the remainder at arrange time), gaps included, clamped to
    /// the bounds; cross axis takes the clamped max of the children's desired
    /// extents.
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
            Size want = _children[i].Measure(constraints);
            int childMain = horizontal
                ? Math.Max(0, want.Width)
                : Math.Max(0, want.Height);
            main += track.IsFlex
                ? track.FlexFloor
                : track.IsAuto
                    ? Math.Min(Math.Max(childMain, track.FlexFloor), track.FlexCap)
                    : (track.IsPercent || track.IsRatio)
                        ? (int)(mainMax * track.Fraction)
                        : track.FixedCells;

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
    /// forwards it. The solver is skipped when the normalized Rect, the track
    /// list and the <c>Auto</c> intrinsics still match the cached entry (#2521);
    /// the child <see cref="INode.Arrange"/> forwarding is never skipped, so a
    /// child still receives its frame every frame. Zero-allocation: the frame
    /// scratch is reused across frames and the key is value types only.
    /// </summary>
    public override void Arrange(Rect rect)
    {
        SetArranged(rect);
        int n = _children.Count;
        if (n == 0)
        {
            return;
        }

        // Declared in this scope so the stackalloc can be handed to the solver
        // directly. Only Auto slots are written; the solver reads no other slot.
        Span<int> intrinsic = n <= 256 ? stackalloc int[n] : new int[n];
        ulong intrinsicSignature = 0;
        if (_hasAuto)
        {
            bool horizontal = Direction == SplitDir.Horizontal;
            var bounds = new Constraints(Arranged.Width, Arranged.Height);
            var hash = new HashCode();
            for (int i = 0; i < n; i++)
            {
                if (!_tracks[i].IsAuto)
                {
                    continue;
                }

                Size want = _children[i].Measure(bounds);
                int value = horizontal ? want.Width : want.Height;
                intrinsic[i] = value;
                hash.Add(i);
                hash.Add(value);
            }

            intrinsicSignature = (uint)hash.ToHashCode();
        }

        if (_hasLayout
            && _layoutRect == Arranged
            && _layoutTracks == _trackSignature
            && _layoutIntrinsic == intrinsicSignature)
        {
            _hits++;
        }
        else
        {
            FlexLayout.Arrange(
                Direction,
                Arranged,
                CollectionsMarshal.AsSpan(_tracks),
                intrinsic,
                CollectionsMarshal.AsSpan(_frames),
                Gap,
                Justify);
            _layoutRect = Arranged;
            _layoutTracks = _trackSignature;
            _layoutIntrinsic = intrinsicSignature;
            _hasLayout = true;
            _misses++;
        }

        for (int i = 0; i < n; i++)
        {
            _children[i].Arrange(_frames[i]);
        }
    }

    /// <summary>
    /// Recomputes the setup-time track fingerprint and drops the cached solve.
    /// Called from the constructor and every <see cref="Add"/> / <see cref="Remove"/>.
    /// </summary>
    private void RefreshTrackSignature()
    {
        var hash = new HashCode();
        hash.Add(_tracks.Count);
        hash.Add(Direction);
        hash.Add(Gap);
        hash.Add(Justify);
        _hasAuto = false;
        for (int i = 0; i < _tracks.Count; i++)
        {
            FlexTrack track = _tracks[i];
            _hasAuto |= track.IsAuto;
            hash.Add(track);
        }

        _trackSignature = (uint)hash.ToHashCode();
        _hasLayout = false;
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
