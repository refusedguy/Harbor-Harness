using System.Text;
using Harbor.Ui.Framework.Rendering.Input;

namespace Harbor.Ui.Framework.Rendering.Widgets;

/// <summary>
/// One node of a <see cref="TreeView" />: an immutable label plus an ordered
/// child list. Expansion is the only mutable bit (a mutable card like the
/// tool-call block: the host marks the timeline slot dirty after a toggle —
/// here via <see cref="TreeView.Changed" />).
/// </summary>
public sealed class TreeNode
{
    public TreeNode(string label, IEnumerable<TreeNode>? children = null, bool expanded = false)
    {
        Label = string.IsNullOrWhiteSpace(label) ? "?" : label.Trim();
        Children = children is null ? [] : [.. children];
        IsExpanded = expanded;
    }

    /// <summary>Single-line display text (trimmed, never empty).</summary>
    public string Label { get; }

    /// <summary>Ordered children; empty for leaves.</summary>
    public IReadOnlyList<TreeNode> Children { get; }

    /// <summary>True when <see cref="Children" /> is non-empty.</summary>
    public bool HasChildren => Children.Count > 0;

    /// <summary>True when children participate in the visible row list.</summary>
    public bool IsExpanded { get; private set; }

    /// <summary>Show children (no-op for leaves).</summary>
    public void Expand()
    {
        if (HasChildren)
        {
            IsExpanded = true;
        }
    }

    /// <summary>Hide children (no-op for leaves).</summary>
    public void Collapse()
    {
        if (HasChildren)
        {
            IsExpanded = false;
        }
    }

    /// <summary>Flip expansion; returns the new state (leaves stay collapsed).</summary>
    public bool Toggle()
    {
        if (!HasChildren)
        {
            return false;
        }

        IsExpanded = !IsExpanded;
        return IsExpanded;
    }
}

/// <summary>
/// One visible row of a <see cref="TreeView" />: the node plus its nesting depth.
/// </summary>
public readonly record struct TreeRow(TreeNode Node, int Depth);

/// <summary>
/// Expandable tree block (bubbles tree / Textual Tree pattern): pre-order
/// visible rows over expandable nodes with a cursor, painted with guide
/// glyphs (<c>▾</c> expanded, <c>▸</c> collapsed, <c>·</c> leaf) and two-space
/// indent per depth. Needed by the UX5 child transcript and the future
/// file-tree; intentionally host-agnostic — selection actions beyond
/// expand/collapse belong to the host (Enter/Space on a leaf is NOT consumed).
/// Implements <see cref="IFocusTarget" /> so the host <c>FocusRouter</c> can
/// traverse it via Tab; the cursor row is highlighted and the label turns
/// accent-bold while focused.
/// </summary>
public sealed class TreeView : IChatBlock, IFocusTarget
{
    private static long _nextId;
    private readonly long _id = Interlocked.Increment(ref _nextId);

    private readonly IReadOnlyList<TreeNode> _roots;
    private readonly int _budgetBytes;
    private readonly List<TreeRow> _visible = [];

    private bool _focused;
    private bool _dirty = true;
    private int _cursor;
    private int _lastHeight = 1;

    /// <summary>Screen-space clip rect from the last <see cref="Paint" /> pass.</summary>
    internal Rect? LastPaintRect { get; private set; }

    public TreeView(IEnumerable<TreeNode>? roots = null)
    {
        _roots = roots is null ? [] : [.. roots];
        int bytes = 64;
        for (int i = 0; i < _roots.Count; i++)
        {
            bytes += NodeBytes(_roots[i]);
        }

        _budgetBytes = bytes;
    }

    /// <summary>Stable router id — unique per tree instance.</summary>
    public string Id => $"tree:{_id}";

    public void OnFocusChanged(bool focused) => _focused = focused;

    /// <summary>True while this tree holds keyboard focus.</summary>
    public bool Focused => _focused;

    public string Kind => "tree";

    public bool IsStreamContinuation => false;

    public int BudgetBytes => _budgetBytes;

    /// <summary>Forest roots (never null).</summary>
    public IReadOnlyList<TreeNode> Roots => _roots;

    /// <summary>Visible (pre-order, expansion-aware) row count.</summary>
    public int VisibleCount
    {
        get
        {
            EnsureVisible();
            return _visible.Count;
        }
    }

    /// <summary>Cursor position inside the visible rows (clamped).</summary>
    public int CursorIndex
    {
        get
        {
            EnsureVisible();
            return _visible.Count == 0 ? 0 : Math.Min(_cursor, _visible.Count - 1);
        }
    }

    /// <summary>Cached visible rows (rebuilt on expand/collapse).</summary>
    public IReadOnlyList<TreeRow> VisibleRows
    {
        get
        {
            EnsureVisible();
            return _visible;
        }
    }

    /// <summary>Raised on cursor moves and expand/collapse so the host can mark the slot dirty.</summary>
    public event EventHandler? Changed;

    public BlockMeasure Measure(int width)
    {
        EnsureVisible();
        return BlockMeasure.Exact(Math.Max(1, _visible.Count));
    }

    public int CheapEstimate(int width) => _dirty ? TotalNodes(_roots) : Math.Max(1, _visible.Count);

    public void Paint(in BlockPaintContext ctx)
    {
        var buffer = ctx.Buffer;
        int width = ctx.Rect.Width;
        int height = ctx.Rect.Height;
        if (width <= 0 || height <= 0)
        {
            return;
        }

        EnsureVisible();
        LastPaintRect = ctx.Rect;
        _lastHeight = height;

        int total = Math.Max(1, _visible.Count);
        int start = Math.Min(ctx.SkipRows, total);
        int count = Math.Min(height, total - start);

        var cursorBg = Cell.From(new Rune(' '), new CellStyle(bg: ChatPalette.Surface2));
        for (int i = 0; i < count; i++)
        {
            int row = start + i;
            int y = ctx.Rect.Y + i;
            if (_visible.Count == 0)
            {
                buffer.SetText(ctx.Rect.X, y, "(empty)", ChatPalette.Dim);
                break;
            }

            var (node, depth) = _visible[row];
            bool cursor = row == CursorIndex;
            if (cursor)
            {
                buffer.Fill(new Rect(ctx.Rect.X, y, width, 1), in cursorBg);
            }

            int x = ctx.Rect.X + (depth * 2);
            int right = ctx.Rect.Right;
            if (x >= right)
            {
                continue;
            }

            string glyph = node.HasChildren ? (node.IsExpanded ? "▾ " : "▸ ") : "· ";
            buffer.SetText(x, y, glyph, ChatPalette.Dim);
            x += 2;
            if (x >= right)
            {
                continue;
            }

            var labelStyle = cursor && _focused
                ? new CellStyle(ChatPalette.Accent, attrs: StyleAttr.Bold)
                : ChatPalette.ToolArgs;
            string label = node.Label;
            int avail = right - x;
            if (label.Length > avail)
            {
                label = label[..avail];
            }

            if (label.Length > 0)
            {
                buffer.SetText(x, y, label, labelStyle);
            }
        }
    }

    public string RawText()
    {
        EnsureVisible();
        if (_visible.Count == 0)
        {
            return "(empty)";
        }

        var sb = new StringBuilder();
        for (int i = 0; i < _visible.Count; i++)
        {
            if (i > 0)
            {
                sb.Append('\n');
            }

            var (node, depth) = _visible[i];
            sb.Append(' ', depth * 2);
            sb.Append(node.HasChildren ? (node.IsExpanded ? "▾ " : "▸ ") : "· ");
            sb.Append(node.Label);
        }

        return sb.ToString();
    }

    /// <summary>
    /// Route one key event (press/repeat only, no modifiers). Up/Down/k/j move,
    /// Home/End jump, PageUp/PageDown move a viewport, Right/l expands or
    /// descends, Left/h collapses or ascends, Enter/Space toggles a parent.
    /// Returns true when the key was consumed.
    /// </summary>
    public bool HandleKey(in KeyEvent key)
    {
        if ((key.EventType != KeyEventType.Press && key.EventType != KeyEventType.Repeat)
            || key.Modifiers != KeyModifiers.None)
        {
            return false;
        }

        EnsureVisible();
        if (_visible.Count == 0)
        {
            return false;
        }

        switch (key.Key)
        {
            case KeyCode.Up:
                return Move(-1);
            case KeyCode.Down:
                return Move(1);
            case KeyCode.Home:
                return MoveTo(0);
            case KeyCode.End:
                return MoveTo(_visible.Count - 1);
            case KeyCode.PageUp:
                return Move(-Math.Max(1, _lastHeight));
            case KeyCode.PageDown:
                return Move(Math.Max(1, _lastHeight));
            case KeyCode.Right:
                return ExpandOrDescend();
            case KeyCode.Left:
                return CollapseOrAscend();
            case KeyCode.Enter:
                return ToggleAtCursor();
            case KeyCode.Char:
                return HandleChar(key.Character);
            default:
                return false;
        }
    }

    /// <summary>Expand every parent in the forest.</summary>
    public void ExpandAll()
    {
        bool changed = false;
        for (int i = 0; i < _roots.Count; i++)
        {
            changed |= ExpandDeep(_roots[i]);
        }

        if (changed)
        {
            _dirty = true;
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Collapse every node in the forest (cursor clamps to a root).</summary>
    public void CollapseAll()
    {
        bool changed = false;
        for (int i = 0; i < _roots.Count; i++)
        {
            changed |= CollapseDeep(_roots[i]);
        }

        if (changed)
        {
            _dirty = true;
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    private bool HandleChar(Rune c)
    {
        // Space toggles like Enter; letters are case-insensitive (vim + arrows both work).
        if (c.Value == ' ')
        {
            return ToggleAtCursor();
        }

        return Rune.ToUpperInvariant(c).Value switch
        {
            'J' => Move(1),
            'K' => Move(-1),
            'H' => CollapseOrAscend(),
            'L' => ExpandOrDescend(),
            _ => false,
        };
    }

    private bool Move(int delta)
    {
        int target = Math.Clamp(_cursor + delta, 0, _visible.Count - 1);
        if (target == _cursor)
        {
            return true; // clamped at an edge — still consumed (list behavior)
        }

        _cursor = target;
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    private bool MoveTo(int index)
    {
        int target = Math.Clamp(index, 0, _visible.Count - 1);
        if (target == _cursor)
        {
            return true;
        }

        _cursor = target;
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    private bool ToggleAtCursor()
    {
        var node = _visible[CursorIndex].Node;
        if (!node.HasChildren)
        {
            return false; // leaves don't toggle — the host owns Enter/Space there
        }

        node.Toggle();
        _dirty = true;
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    private bool ExpandOrDescend()
    {
        var node = _visible[CursorIndex].Node;
        if (!node.HasChildren)
        {
            return false;
        }

        if (!node.IsExpanded)
        {
            node.Expand();
            _dirty = true;
            Changed?.Invoke(this, EventArgs.Empty);
            return true;
        }

        return MoveTo(CursorIndex + 1); // first child is the next visible row
    }

    private bool CollapseOrAscend()
    {
        var (node, depth) = _visible[CursorIndex];
        if (node.HasChildren && node.IsExpanded)
        {
            node.Collapse();
            _dirty = true;
            Changed?.Invoke(this, EventArgs.Empty);
            return true;
        }

        // Already collapsed/leaf: ascend to the nearest visible ancestor.
        for (int i = CursorIndex - 1; i >= 0; i--)
        {
            if (_visible[i].Depth == depth - 1)
            {
                return MoveTo(i);
            }
        }

        return false; // root row — nothing to ascend to
    }

    private void EnsureVisible()
    {
        if (!_dirty)
        {
            return;
        }

        _dirty = false;
        _visible.Clear();
        for (int i = 0; i < _roots.Count; i++)
        {
            Append(_roots[i], 0);
        }

        if (_cursor >= _visible.Count)
        {
            _cursor = Math.Max(0, _visible.Count - 1);
        }
    }

    private void Append(TreeNode node, int depth)
    {
        _visible.Add(new TreeRow(node, depth));
        if (node.IsExpanded)
        {
            for (int i = 0; i < node.Children.Count; i++)
            {
                Append(node.Children[i], depth + 1);
            }
        }
    }

    private static bool ExpandDeep(TreeNode node)
    {
        bool changed = false;
        if (node.HasChildren && !node.IsExpanded)
        {
            node.Expand();
            changed = true;
        }

        for (int i = 0; i < node.Children.Count; i++)
        {
            changed |= ExpandDeep(node.Children[i]);
        }

        return changed;
    }

    private static bool CollapseDeep(TreeNode node)
    {
        bool changed = false;
        if (node.IsExpanded)
        {
            node.Collapse();
            changed = true;
        }

        for (int i = 0; i < node.Children.Count; i++)
        {
            changed |= CollapseDeep(node.Children[i]);
        }

        return changed;
    }

    private static int TotalNodes(IReadOnlyList<TreeNode> nodes)
    {
        int total = 0;
        for (int i = 0; i < nodes.Count; i++)
        {
            total += 1 + TotalNodes(nodes[i].Children);
        }

        return Math.Max(1, total);
    }

    private static int NodeBytes(TreeNode node)
    {
        int bytes = 16 + (node.Label.Length * 2);
        for (int i = 0; i < node.Children.Count; i++)
        {
            bytes += NodeBytes(node.Children[i]);
        }

        return bytes;
    }
}
