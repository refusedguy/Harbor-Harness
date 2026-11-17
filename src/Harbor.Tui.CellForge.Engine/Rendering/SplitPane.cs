// Issue #2328: generalized resizable split-pane primitive with focus switching
// and a fullscreen toggle. Composes the three existing CellForge primitives
// instead of reinventing them: OverlayLayer seats the fullscreen pane,
// FocusRouter owns Tab-order focus, and KeyBindings (the capture table of
// InputRouting, the KeyRouter) maps gestures to split actions. Pane identity and
// default sizes follow the PanelRegistry id/default-size model without a Harbor
// dependency. Same leaf rule as its neighbours in this directory: BCL-only,
// AOT-compatible, zero Harbor references, render-thread owned, chat untouched.

using Harbor.Tui.CellForge.Input;

namespace Harbor.Tui.CellForge.Rendering;

/// <summary>
/// One pane in a <see cref="SplitPane"/>: a stable id (PanelRegistry model), its
/// content node, a relative <see cref="Weight"/> (resized by dragging a divider)
/// and a <see cref="MinWeight"/> floor. The content node is arranged by the
/// parent; the pane itself is the focus target (one <see cref="IFocusTarget"/>
/// per pane, the same contract <see cref="FocusRouter"/> walks).
/// </summary>
public sealed class SplitPaneItem : IFocusTarget
{
    /// <summary>Default minimum weight when none is supplied.</summary>
    public const int DefaultMinWeight = 1;

    private int _weight;
    private int _minWeight;

    public SplitPaneItem(string id, INode content, int weight = 1, int minWeight = DefaultMinWeight)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(content);
        ArgumentOutOfRangeException.ThrowIfLessThan(weight, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(minWeight, 1);
        Id = id;
        Content = content;
        _minWeight = minWeight;
        _weight = Math.Max(weight, minWeight);
    }

    /// <inheritdoc />
    public string Id { get; }

    /// <summary>Painted content, arranged by the owning <see cref="SplitPane"/>.</summary>
    public INode Content { get; }

    /// <summary>Relative main-axis share. Mutated only through split resize.</summary>
    public int Weight => _weight;

    /// <summary>Lower bound on <see cref="Weight"/> enforced by resize.</summary>
    public int MinWeight => _minWeight;

    /// <summary>Whether this pane currently owns focus (set through focus routing).</summary>
    public bool IsFocused { get; private set; }

    /// <inheritdoc />
    public void OnFocusChanged(bool focused) => IsFocused = focused;

    internal void SetWeight(int weight) => _weight = Math.Max(weight, _minWeight);
}

/// <summary>
/// Resizable n-ary split panes with focus traversal and a fullscreen toggle.
/// Generalizes the chat panel dock: panes carry stable ids and default weights
/// (PanelRegistry model), focus moves through <see cref="FocusRouter"/>, keys map
/// to actions through <see cref="KeyBindings"/> (the KeyRouter capture table),
/// and fullscreen is a modal <see cref="ZLayer"/> in an <see cref="OverlayLayer"/>.
/// Setup-time mutation only (Add/Remove); the steady-state frame path allocates
/// nothing. Render-thread owned, like every engine node.
/// </summary>
public sealed class SplitPane : ContainerNode
{
    /// <summary>Action id for Tab (focus next pane).</summary>
    public const string ActionFocusNext = "split.focus_next";

    /// <summary>Action id for Shift+Tab (focus previous pane).</summary>
    public const string ActionFocusPrevious = "split.focus_prev";

    /// <summary>Action id for the fullscreen toggle key.</summary>
    public const string ActionFullscreen = "split.fullscreen";

    /// <summary>Action id for the grow/shrink resize keys.</summary>
    public const string ActionGrow = "split.grow";

    /// <summary>Action id for the grow/shrink resize keys.</summary>
    public const string ActionShrink = "split.shrink";

    private const string FullscreenLayerId = "__split_fullscreen";

    private readonly List<SplitPaneItem> _items = [];
    private readonly List<Rect> _frames = [];
    private readonly FocusRouter _focus = new();
    private readonly OverlayLayer _fullscreen = new();
    private readonly KeyBindings _bindings = new();

    public SplitPane(SplitDir direction, int gap = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(gap);
        Direction = direction;
        Gap = gap;
        BindDefaults();
    }

    /// <summary>Main axis: side-by-side columns vs stacked rows.</summary>
    public SplitDir Direction { get; }

    /// <summary>Base cells between adjacent panes.</summary>
    public int Gap { get; }

    /// <summary>Panes in split order.</summary>
    public IReadOnlyList<SplitPaneItem> Items => _items;

    /// <summary>The router that owns focus traversal (the focus half of the key router).</summary>
    public FocusRouter FocusRouter => _focus;

    /// <summary>The overlay compositor that holds the fullscreen pane.</summary>
    public OverlayLayer FullscreenLayer => _fullscreen;

    /// <summary>The capture binding table (KeyRouter) driving <see cref="HandleKey"/>.</summary>
    public KeyBindings Bindings => _bindings;

    /// <summary>Currently focused pane, or null before the first focus move.</summary>
    public SplitPaneItem? Focused => _focus.Current as SplitPaneItem;

    /// <summary>Pane currently shown fullscreen, or null in split view.</summary>
    public SplitPaneItem? FullscreenItem { get; private set; }

    /// <summary>Whether the fullscreen pane is active.</summary>
    public bool IsFullscreen => FullscreenItem is not null;

    /// <summary>Setup-time: appends a pane and seats it in the focus order.</summary>
    public SplitPaneItem Add(string id, INode content, int weight = 1, int minWeight = SplitPaneItem.DefaultMinWeight)
    {
        var item = new SplitPaneItem(id, content, weight, minWeight);
        _items.Add(item);
        _frames.Add(default);
        _focus.Add(item);
        return item;
    }

    /// <summary>Setup-time: removes a pane by id. Clears fullscreen if it was shown.</summary>
    public bool Remove(string id)
    {
        int index = IndexOf(id);
        if (index < 0)
        {
            return false;
        }

        SplitPaneItem item = _items[index];
        if (ReferenceEquals(item, FullscreenItem))
        {
            _ = ExitFullscreen();
        }

        _items.RemoveAt(index);
        _frames.RemoveAt(index);
        return _focus.Remove(id);
    }

    /// <summary>
    /// Resizes the divider after pane <paramref name="index"/> by
    /// <paramref name="delta"/>: a positive delta grows that pane and shrinks its
    /// neighbour, conserving their combined weight and honouring both floors.
    /// Returns the delta actually applied to the named pane.
    /// </summary>
    public int ResizeDivider(int index, int delta)
    {
        if (index < 0 || index >= _items.Count - 1)
        {
            return 0;
        }

        SplitPaneItem left = _items[index];
        SplitPaneItem right = _items[index + 1];
        return ResizePair(left, right, delta);
    }

    /// <summary>
    /// Resizes pane <paramref name="id"/> by <paramref name="delta"/>, taking the
    /// space from (or giving it to) its neighbour. The last pane uses the divider
    /// before it, so a positive delta always grows the named pane.
    /// Returns the delta actually applied.
    /// </summary>
    public int Resize(string id, int delta)
    {
        int index = IndexOf(id);
        if (index < 0 || _items.Count < 2)
        {
            return 0;
        }

        if (index == _items.Count - 1)
        {
            // Last pane: the divider before it, inverted (growing last shrinks previous).
            return -ResizePair(_items[index - 1], _items[index], -delta);
        }

        return ResizePair(_items[index], _items[index + 1], delta);
    }

    private static int ResizePair(SplitPaneItem left, SplitPaneItem right, int delta)
    {
        int total = left.Weight + right.Weight;
        int lo = left.MinWeight;
        int hi = total - right.MinWeight;
        if (hi < lo)
        {
            hi = lo;
        }

        int newLeft = Math.Clamp(left.Weight + delta, lo, hi);
        int applied = newLeft - left.Weight;
        if (applied == 0)
        {
            return 0;
        }

        left.SetWeight(newLeft);
        right.SetWeight(total - newLeft);
        return applied;
    }

    /// <summary>Focus the next pane in Tab order. Wraps around.</summary>
    public bool FocusNext() => _focus.Next();

    /// <summary>Focus the previous pane in Tab order. Wraps around.</summary>
    public bool FocusPrevious() => _focus.Previous();

    /// <summary>Focus the pane with the given id.</summary>
    public bool Focus(string id) => _focus.FocusById(id);

    /// <summary>Focus a pane by index.</summary>
    public bool FocusIndex(int index) => _focus.Jump(index);

    /// <summary>
    /// Toggles the focused pane fullscreen. The first call pushes it as a modal
    /// layer in <see cref="FullscreenLayer"/>; the next restores the split view.
    /// Returns true when a pane is fullscreen afterwards.
    /// </summary>
    public bool ToggleFullscreen()
    {
        if (FullscreenItem is not null)
        {
            return ExitFullscreen();
        }

        SplitPaneItem? item = Focused;
        if (item is null)
        {
            return false;
        }

        FullscreenItem = item;
        _fullscreen.Push(new ZLayer(FullscreenLayerId, item.Content, Arranged.Width > 0 && Arranged.Height > 0
            ? Arranged
            : default, z: 0, modal: true));
        return true;
    }

    /// <summary>Leaves fullscreen. Returns false (the new state) when it was not active.</summary>
    public bool ExitFullscreen()
    {
        if (FullscreenItem is null)
        {
            return false;
        }

        _ = _fullscreen.Remove(FullscreenLayerId);
        FullscreenItem = null;
        return false;
    }

    /// <summary>
    /// Routes one key through the KeyRouter capture table and performs the mapped
    /// split action. Returns true when the key was consumed. Resize actions act on
    /// the focused pane; focus/fullscreen actions need no argument.
    /// </summary>
    public bool HandleKey(in KeyEvent key)
    {
        if (!_bindings.TryMatch(key, out string? action) || action is null)
        {
            return false;
        }

        switch (action)
        {
            case ActionFocusNext:
                return FocusNext();
            case ActionFocusPrevious:
                return FocusPrevious();
            case ActionFullscreen:
                _ = ToggleFullscreen();
                return true;
            case ActionGrow:
                return Focused is not null && Resize(Focused.Id, 1) != 0;
            case ActionShrink:
                return Focused is not null && Resize(Focused.Id, -1) != 0;
            default:
                return false;
        }
    }

    private void BindDefaults()
    {
        _bindings.Bind(KeyGesture.ForKey(KeyCode.Tab), ActionFocusNext);
        _bindings.Bind(KeyGesture.ForKey(KeyCode.Tab, KeyModifiers.Shift), ActionFocusPrevious);
        _bindings.Bind(KeyGesture.ForChar('f'), ActionFullscreen);
        _bindings.Bind(KeyGesture.ForChar('='), ActionGrow);
        _bindings.Bind(KeyGesture.ForChar('+'), ActionGrow);
        _bindings.Bind(KeyGesture.ForChar('-'), ActionShrink);
    }

    /// <inheritdoc />
    public override Size Measure(Constraints constraints)
    {
        if (FullscreenItem is not null)
        {
            Size want = FullscreenItem.Content.Measure(constraints);
            return new Size(Math.Min(want.Width, constraints.Width), Math.Min(want.Height, constraints.Height));
        }

        bool horizontal = Direction == SplitDir.Horizontal;
        int mainMax = horizontal ? constraints.Width : constraints.Height;
        int crossMax = horizontal ? constraints.Height : constraints.Width;
        int main = 0;
        int cross = 0;
        for (int i = 0; i < _items.Count; i++)
        {
            Size want = _items[i].Content.Measure(constraints);
            main += _items[i].MinWeight;
            int childCross = horizontal ? want.Height : want.Width;
            if (childCross > cross)
            {
                cross = childCross;
            }
        }

        if (_items.Count > 1)
        {
            main += Gap * (_items.Count - 1);
        }

        return horizontal
            ? new Size(Math.Min(main, mainMax), Math.Min(cross, crossMax))
            : new Size(Math.Min(cross, crossMax), Math.Min(main, mainMax));
    }

    /// <inheritdoc />
    public override void Arrange(Rect rect)
    {
        SetArranged(rect);
        int n = _items.Count;
        if (n == 0)
        {
            return;
        }

        bool horizontal = Direction == SplitDir.Horizontal;
        int mainExtent = horizontal ? Arranged.Width : Arranged.Height;
        int gapTotal = n > 1 ? Gap * (n - 1) : 0;
        int available = Math.Max(0, mainExtent - gapTotal);

        int[] sizes = Allocate(available);
        int cursor = horizontal ? Arranged.X : Arranged.Y;
        for (int i = 0; i < n; i++)
        {
            Rect frame = horizontal
                ? new Rect(cursor, Arranged.Y, sizes[i], Arranged.Height)
                : new Rect(Arranged.X, cursor, Arranged.Width, sizes[i]);
            _frames[i] = frame;
            _items[i].Content.Arrange(frame);
            cursor += sizes[i] + Gap;
        }

        if (FullscreenItem is null)
        {
            return;
        }

        ZLayer? layer = _fullscreen.Get(FullscreenLayerId);
        if (layer is not null)
        {
            layer.Bounds = Arranged;
        }

        _fullscreen.Arrange(Arranged);
    }

    /// <summary>
    /// Forwards prepaint to panes by their stable <see cref="SplitPaneItem.Id"/>
    /// and to the fullscreen layer by its own id, so resizing or focus changes
    /// do not change a pane's element identity.
    /// </summary>
    public override PrepaintRecord Prepaint(PrepaintContext context, ElementId id)
    {
        ArgumentNullException.ThrowIfNull(context);
        for (int i = 0; i < _items.Count; i++)
        {
            _ = _items[i].Content.Prepaint(context, id.Child(_items[i].Id));
        }

        _ = _fullscreen.Prepaint(context, id.Child(FullscreenLayerId));
        return PrepaintRecord.Empty;
    }

    /// <summary>
    /// Water-fills the main axis: every pane gets its floor first, then the
    /// remainder is split by weight with a largest-remainder pass for the cells
    /// integer division leaves behind. Total is conserved; no allocation on the
    /// frame path beyond the small scratch arrays (setup-time bounded).
    /// </summary>
    private int[] Allocate(int available)
    {
        int n = _items.Count;
        var sizes = new int[n];
        if (n == 0 || available <= 0)
        {
            return sizes;
        }

        int floor = available / n;
        int floors = 0;
        for (int i = 0; i < n; i++)
        {
            int min = Math.Min(_items[i].MinWeight, floor);
            sizes[i] = min;
            floors += min;
        }

        int remainder = available - floors;
        if (remainder <= 0)
        {
            return sizes;
        }

        long weightSum = 0;
        for (int i = 0; i < n; i++)
        {
            weightSum += _items[i].Weight;
        }

        if (weightSum <= 0)
        {
            weightSum = n;
            for (int i = 0; i < n; i++)
            {
                sizes[i] += remainder / n;
            }

            sizes[0] += remainder - (remainder / n) * n;
            return sizes;
        }

        var fractions = new (int Index, int Rem)[n];
        int given = 0;
        for (int i = 0; i < n; i++)
        {
            long numerator = (long)remainder * _items[i].Weight;
            int whole = (int)(numerator / weightSum);
            sizes[i] += whole;
            given += whole;
            fractions[i] = (i, (int)(numerator % weightSum));
        }

        int leftover = remainder - given;
        // Largest remainder wins the spare cells; ties go to the earlier pane
        // so the seat is deterministic (Array.Sort is not stable).
        Array.Sort(fractions, static (a, b) =>
        {
            int byRem = b.Rem.CompareTo(a.Rem);
            return byRem != 0 ? byRem : a.Index.CompareTo(b.Index);
        });
        for (int i = 0; i < leftover && i < n; i++)
        {
            sizes[fractions[i].Index]++;
        }

        return sizes;
    }

    /// <inheritdoc />
    public override void Paint(ScreenBuffer buffer, Rect clip)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        if (FullscreenItem is not null)
        {
            _fullscreen.Paint(buffer, clip);
            return;
        }

        Rect viewport = Arranged.Intersect(clip);
        for (int i = 0; i < _items.Count; i++)
        {
            Rect area = _frames[i].Intersect(viewport);
            if (area.Width > 0 && area.Height > 0)
            {
                _items[i].Content.Paint(buffer, area);
            }
        }
    }

    private int IndexOf(string id)
    {
        for (int i = 0; i < _items.Count; i++)
        {
            if (string.Equals(_items[i].Id, id, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }
}
