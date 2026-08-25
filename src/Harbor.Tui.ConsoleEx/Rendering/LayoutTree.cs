using System.Text;

namespace Harbor.Tui.ConsoleEx.Rendering;

/// <summary>Splits a node's usable extent along its main axis.</summary>
public enum SplitDir : byte
{
    /// <summary>Children share the width (side-by-side columns).</summary>
    Horizontal = 0,

    /// <summary>Children share the height (stacked rows).</summary>
    Vertical = 1,
}

/// <summary>
/// Leaf panel of the layout tree (celldiff §5): owns a resolved
/// <see cref="Rect"/>, minimum sizes, collapse priority and focus flag.
/// Lower <see cref="Priority"/> collapses first; status bars use int.MaxValue.
/// </summary>
public abstract class Panel
{
    protected Panel(string id, Size min, int priority)
    {
        Id = id;
        Min = min;
        Priority = priority;
    }

    public string Id { get; }
    public Size Min { get; }
    public int Priority { get; }

    /// <summary>Set by the layout solver each frame; read by painters/routing.</summary>
    public Rect Rect { get; internal set; }

    public bool Focused { get; internal set; }

    public abstract void Paint(ScreenBuffer buffer);

    /// <summary>Minimum extent along a split direction.</summary>
    internal int MinAlong(SplitDir dir) => dir == SplitDir.Horizontal ? Min.Width : Min.Height;
}

/// <summary>Immutable size pair for panel minimums.</summary>
public readonly record struct Size(int Width, int Height);

internal sealed class SplitNode
{
    public SplitNode(Panel leaf)
    {
        Leaf = leaf;
    }

    public Panel? Leaf { get; set; }

    public SplitDir Dir { get; set; } = SplitDir.Horizontal;

    /// <summary>Share of the usable extent given to child A.</summary>
    public float Ratio { get; set; } = 0.5f;

    public byte GapSize { get; set; } = 1;

    public SplitNode? A { get; set; }
    public SplitNode? B { get; set; }

    public int MinAlong(SplitDir dir) => Leaf is not null
        ? Leaf.MinAlong(dir)
        : A!.MinAlong(dir) + B!.MinAlong(dir) + GapSize;

    public IEnumerable<Panel> Panels()
    {
        if (Leaf is not null)
        {
            yield return Leaf;
            yield break;
        }

        foreach (var p in A!.Panels())
        {
            yield return p;
        }

        foreach (var p in B!.Panels())
        {
            yield return p;
        }
    }
}

/// <summary>
/// Binary split tree with water-filling solver honoring per-panel minimums
/// and priority-based collapse (celldiff §5.1). Results are cached by
/// (width, height, capsVer) — repeated frames at the same geometry solve in
/// O(1); any tree mutation bumps capsVer.
/// </summary>
public sealed class LayoutTree
{
    private readonly Dictionary<string, Panel> _panels = [];
    private SplitNode? _root;
    private uint _capsVer = 1;

    private (int W, int H, uint Ver)? _cacheKey;
    private readonly List<Rect> _cachedRects = [];

    public IReadOnlyCollection<Panel> Panels => _panels.Values;

    public void AddRoot(Panel panel)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(panel.Id);
        _root = new SplitNode(panel);
        Register(panel);
    }

    /// <summary>Splits the side containing <paramref name="panelId"/>: the old
    /// panel keeps ratio·(usable−gap−newMin), the new one takes the rest.</summary>
    public void Split(string panelId, SplitDir dir, float ratio, Panel newPanel, byte gap = 1)
    {
        var target = FindAndWrap(_root, panelId) ?? throw new KeyNotFoundException($"panel '{panelId}' not found");
        target.Dir = dir;
        target.Ratio = ratio;
        target.GapSize = gap;
        target.A = target.Leaf is not null ? new SplitNode(target.Leaf) : target.A;
        target.B = new SplitNode(newPanel);
        target.Leaf = null;
        Register(newPanel);
        _capsVer++;
    }

    public void Remove(string panelId)
    {
        if (_root?.Leaf?.Id == panelId)
        {
            _panels.Remove(panelId);
            _root = null;
            _capsVer++;
            return;
        }

        RemoveFrom(_root, panelId);
        _capsVer++;
    }

    /// <summary>Resolves every panel rect for the given viewport. Cached.</summary>
    public void Solve(int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(width);
        ArgumentOutOfRangeException.ThrowIfNegative(height);

        if (_cacheKey == (width, height, _capsVer))
        {
            ApplyCached();
            return;
        }

        _cachedRects.Clear();
        if (_root is not null && width > 0 && height > 0)
        {
            SolveNode(_root, new Rect(0, 0, width, height));
        }

        // Snapshot rects in stable panel order for cache replay.
        _solvedOrder.Clear();
        foreach (var panel in Ordered())
        {
            _solvedOrder.Add(panel.Rect);
        }

        _cacheKey = (width, height, _capsVer);
        ApplyCached();
        _focusedId = _panels.Values.FirstOrDefault(p => p.Focused)?.Id;
    }

    private readonly List<Panel> _orderedBuffer = [];
    private readonly List<Rect> _solvedOrder = [];
    private string? _focusedId;

    private IEnumerable<Panel> Ordered()
    {
        _orderedBuffer.Clear();
        if (_root is not null)
        {
            Collect(_root, _orderedBuffer);
        }

        return _orderedBuffer;
    }

    private static void Collect(SplitNode node, List<Panel> into)
    {
        if (node.Leaf is not null)
        {
            into.Add(node.Leaf);
            return;
        }

        if (node.A is not null)
        {
            Collect(node.A, into);
        }

        if (node.B is not null)
        {
            Collect(node.B, into);
        }
    }

    private void ApplyCached()
    {
        var panels = Ordered().ToArray();
        for (int i = 0; i < panels.Length && i < _solvedOrder.Count; i++)
        {
            panels[i].Rect = _solvedOrder[i];
        }

        if (_focusedId is not null && _panels.TryGetValue(_focusedId, out var focused))
        {
            focused.Focused = true;
        }
    }

    private Rect SolveNode(SplitNode node, Rect avail)
    {
        if (node.Leaf is not null)
        {
            node.Leaf.Rect = avail;
            return avail;
        }

        bool horizontal = node.Dir == SplitDir.Horizontal;
        int total = horizontal ? avail.Width : avail.Height;
        int gap = Math.Min(node.GapSize, total);
        int usable = total - gap;

        int minA = node.A!.MinAlong(node.Dir);
        int minB = node.B!.MinAlong(node.Dir);

        // Collapse: when children cannot both fit, sacrifice the lower priority.
        if (usable < minA + minB)
        {
            var winner = PickWinner(node);
            var loser = ReferenceEquals(winner, node.A) ? node.B : node.A;
            CollapseAll(loser);
            return SolveNode(winner, avail);
        }

        int rawA = (int)MathF.Round(usable * node.Ratio);
        int clampedA = Math.Clamp(rawA, minA, usable - minB);
        int clampedB = usable - clampedA;

        Rect rectA, rectB;
        if (horizontal)
        {
            rectA = new Rect(avail.X, avail.Y, clampedA, avail.Height);
            rectB = new Rect(avail.X + clampedA + gap, avail.Y, clampedB, avail.Height);
        }
        else
        {
            rectA = new Rect(avail.X, avail.Y, avail.Width, clampedA);
            rectB = new Rect(avail.X, avail.Y + clampedA + gap, avail.Width, clampedB);
        }

        _ = SolveNode(node.A!, rectA);
        _ = SolveNode(node.B!, rectB);
        return avail;
    }

    private static SplitNode PickWinner(SplitNode node) =>
        PriorityOf(node.A!) >= PriorityOf(node.B!) ? node.A! : node.B!;

    private static int PriorityOf(SplitNode n) => n.Leaf is not null ? n.Leaf.Priority : int.MinValue;

    private static void CollapseAll(SplitNode node)
    {
        if (node.Leaf is not null)
        {
            node.Leaf.Rect = default;
            return;
        }

        CollapseAll(node.A!);
        CollapseAll(node.B!);
    }

    private SplitNode? FindAndWrap(SplitNode? node, string id)
    {
        if (node is null)
        {
            return null;
        }

        if (node.Leaf?.Id == id)
        {
            return node;
        }

        return FindAndWrap(node.A, id) ?? FindAndWrap(node.B, id);
    }

    private bool RemoveFrom(SplitNode? node, string id)
    {
        if (node is null || node.Leaf is not null)
        {
            return false;
        }

        if (node.A?.Leaf?.Id == id)
        {
            Promote(node, keepB: true);
            _panels.Remove(id);
            return true;
        }

        if (node.B?.Leaf?.Id == id)
        {
            Promote(node, keepB: false);
            _panels.Remove(id);
            return true;
        }

        return RemoveFrom(node.A, id) || RemoveFrom(node.B, id);
    }

    private void Promote(SplitNode node, bool keepB)
    {
        var survivor = keepB ? node.B! : node.A!;
        if (survivor.Leaf is not null)
        {
            node.Leaf = survivor.Leaf;
            node.A = null;
            node.B = null;
        }
        else
        {
            node.Dir = survivor.Dir;
            node.Ratio = survivor.Ratio;
            node.GapSize = survivor.GapSize;
            node.A = survivor.A;
            node.B = survivor.B;
        }
    }

    private void Register(Panel panel) => _panels[panel.Id] = panel;
}

/// <summary>
/// Box-drawing frame panel — the minimal concrete painter used by golden
/// grid-dump tests. Focus switches the border to bold accent style.
/// </summary>
public class BorderPanel : Panel
{
    private static readonly CellStyle FrameStyle = new(PackedColor.Indexed(8));
    private static readonly CellStyle FocusedStyle = new(attrs: StyleAttr.Bold);

    public BorderPanel(string id, int minWidth, int minHeight, int priority = 0, string title = "")
        : base(id, new Size(minWidth, minHeight), priority)
    {
        Title = title;
    }

    public string Title { get; set; }

    public override void Paint(ScreenBuffer buffer)
    {
        var r = Rect;
        if (r.Width < 2 || r.Height < 2)
        {
            return;
        }

        var style = Focused ? FocusedStyle : FrameStyle;
        var topLeft = Cell.From(new Rune('┌'), style);
        var topRight = Cell.From(new Rune('┐'), style);
        var bottomLeft = Cell.From(new Rune('└'), style);
        var bottomRight = Cell.From(new Rune('┘'), style);
        var horiz = Cell.From(new Rune('─'), style);
        var vert = Cell.From(new Rune('│'), style);

        int x1 = r.X, y1 = r.Y, x2 = r.Right - 1, y2 = r.Bottom - 1;

        buffer.At(x1, y1) = topLeft;
        buffer.At(x2, y1) = topRight;
        buffer.At(x1, y2) = bottomLeft;
        buffer.At(x2, y2) = bottomRight;

        for (int x = x1 + 1; x < x2; x++)
        {
            buffer.At(x, y1) = horiz;
            buffer.At(x, y2) = horiz;
        }

        for (int y = y1 + 1; y < y2; y++)
        {
            buffer.At(x1, y) = vert;
            buffer.At(x2, y) = vert;
        }

        if (Title.Length > 0 && x2 - x1 > Title.Length + 1)
        {
            buffer.SetText(x1 + 2, y1, Title, style);
        }
    }
}
