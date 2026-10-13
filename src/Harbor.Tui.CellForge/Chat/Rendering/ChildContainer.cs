// RC2 (#1200, epic #1195): heterogeneous Panel child container.
// The engine side already has one (FlexSplitNode over INode, TR1 #1220); the
// Panel side (LayoutTree) only had the binary SplitNode tree plus the
// OverlayStack layer axis — there was no way to put N heterogeneous panels in
// a Row/Column by constraints. This is that seat: direction + (child, track)
// list solved by the shared FlexLayout.Arrange, so forms/dashboards compose
// without touching ChatScreen. Render-thread owned, not thread-safe — the same
// rule as FlexLayout and LayoutTree.

using System.Runtime.InteropServices;
using UIR = Harbor.Ui.Framework.Rendering;
using EngineRect = Harbor.Tui.CellForge.Rendering.Rect;

namespace Harbor.Tui.CellForge.Rendering;

/// <summary>
/// N-ary Row (<see cref="SplitDir.Horizontal"/>) / Column
/// (<see cref="SplitDir.Vertical"/>) of heterogeneous <see cref="Panel"/>
/// children — the Panel-level analogue of <see cref="FlexSplitNode"/>.
/// One <see cref="FlexTrack"/> per child (<c>Fixed</c> / <c>Percent</c> /
/// <c>Fill</c> / <c>Min</c> / <c>Max</c> / <c>Auto</c>); gap and justify behave
/// exactly as in <see cref="FlexLayout"/>. A container is itself a
/// <see cref="Panel"/>, so containers nest (a form section inside a dashboard
/// column) and seat inside <see cref="LayoutTree"/> via
/// <see cref="LayoutTree.AddRoot"/> / <see cref="LayoutTree.Split"/> with no
/// <c>ChatScreen</c> changes: the tree assigns this panel's
/// <see cref="Panel.Rect"/> and the children are (re-)solved lazily on
/// <see cref="Paint"/>.
/// <see cref="Add(Panel, FlexTrack)"/> / <see cref="Remove"/> are setup-time;
/// the steady-state path allocates nothing (<c>for</c> index loops, reused
/// frame scratch via <c>CollectionsMarshal.AsSpan</c>, no LINQ, no closures).
/// <c>Auto</c> tracks degrade to their floor: <see cref="Panel"/> exposes no
/// content-intrinsic measurement, so there is nothing to feed the intrinsic
/// span (the same documented fallback as a missing entry).
/// </summary>
public sealed class ChildContainer : Panel
{
    private readonly List<Panel> _children = [];
    private readonly List<FlexTrack> _tracks = [];
    private readonly List<EngineRect> _frames = [];
    private UIR.Rect _solvedFor;
    private bool _dirty = true;

    /// <summary>
    /// Creates a container. <paramref name="min"/> is the container's own
    /// floor (the base <see cref="Panel.Min"/> is fixed at construction) —
    /// pass the summed child minimums plus gaps when known; per-child floors
    /// along the axis are expressed with <c>Min()</c> / <c>Fixed()</c> tracks.
    /// </summary>
    /// <param name="id">Panel id (non-empty).</param>
    /// <param name="direction">Main axis: Row vs Column.</param>
    /// <param name="min">Container floor; never auto-aggregated from children.</param>
    /// <param name="priority">Collapse priority when nested in a tree.</param>
    /// <param name="gap">Base cells between adjacent children.</param>
    /// <param name="justify">Placement of leftover space on underfill.</param>
    /// <exception cref="ArgumentException"><paramref name="id"/> is empty.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="gap"/> is negative.</exception>
    public ChildContainer(
        string id,
        SplitDir direction,
        Size min,
        int priority = 0,
        int gap = 0,
        FlexJustify justify = FlexJustify.Start)
        : base(id, min, priority)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
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
    public void Add(Panel child, FlexTrack track)
    {
        ArgumentNullException.ThrowIfNull(child);
        _children.Add(child);
        _tracks.Add(track);
        _frames.Add(default);
        _dirty = true;
    }

    /// <summary>Setup-time: removes <paramref name="child"/> and its track/frame.</summary>
    public bool Remove(Panel child)
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
        _dirty = true;
        return true;
    }

    /// <summary>Arranges children into the current <see cref="Panel.Rect"/>.</summary>
    public void Solve()
    {
        _dirty = true;
        EnsureArranged();
    }

    /// <summary>Assigns <see cref="Panel.Rect"/> then arranges children into it.</summary>
    public void Solve(UIR.Rect avail)
    {
        Rect = avail;
        Solve();
    }

    /// <summary>
    /// Paints children in order, skipping zero-area frames (gap-collapse or a
    /// zero-size container). Re-solves first when <see cref="Panel.Rect"/>
    /// moved since the last arrange — this is what makes tree-seating
    /// transparent.
    /// </summary>
    public override void Paint(UIR.ScreenBuffer buffer)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        EnsureArranged();
        for (int i = 0; i < _children.Count; i++)
        {
            var r = _children[i].Rect;
            if (r.Width > 0 && r.Height > 0)
            {
                _children[i].Paint(buffer);
            }
        }
    }

    private void EnsureArranged()
    {
        if (!_dirty && _solvedFor == Rect)
        {
            return;
        }

        var r = Rect;
        var avail = new EngineRect(r.X, r.Y, r.Width, r.Height);
        FlexLayout.Arrange(
            Direction,
            avail,
            CollectionsMarshal.AsSpan(_tracks),
            CollectionsMarshal.AsSpan(_frames),
            Gap,
            Justify);
        for (int i = 0; i < _children.Count; i++)
        {
            var f = _frames[i];
            _children[i].Rect = new UIR.Rect(f.X, f.Y, f.Width, f.Height);
        }

        _solvedFor = r;
        _dirty = false;
    }
}
