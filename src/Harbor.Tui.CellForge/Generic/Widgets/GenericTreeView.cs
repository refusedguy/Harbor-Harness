using System.Collections.Immutable;
using Harbor.Ui.Framework.State;
using Ensure = Harbor.Abstractions.Extensions.Ensure;

namespace Harbor.Tui.CellForge.Generic.Widgets;

/// <summary>
/// One immutable node of a <see cref="GenericTreeView"/>. Expansion is not stored
/// on the node: the view keeps a set of expanded keys, so a refreshed snapshot
/// with fresh node objects keeps the user's open branches.
/// </summary>
/// <param name="Label">Single-line display text.</param>
/// <param name="Key">Stable identity used for expansion and by the preview callback.</param>
/// <param name="IsDirectory">Whether the node can hold children.</param>
/// <param name="IsHidden">Dot-prefixed entry, for the row marker only.</param>
/// <param name="Children">Ordered child nodes; empty for leaves.</param>
public sealed record GenericTreeNode(
    string Label,
    string Key,
    bool IsDirectory,
    bool IsHidden,
    IReadOnlyList<GenericTreeNode> Children)
{
    /// <summary>Build a leaf or parent with explicit children.</summary>
    public GenericTreeNode(string label, string key, bool isDirectory, IReadOnlyList<GenericTreeNode>? children = null, bool isHidden = false)
        : this(label, key, isDirectory, isHidden, children ?? ImmutableArray<GenericTreeNode>.Empty)
    {
    }

    /// <summary>True when the node has children to show.</summary>
    public bool HasChildren => Children.Count > 0;

    /// <summary>
    /// Project one loaded <see cref="FileTreeEntry"/> into the neutral node
    /// shape. The loader's snapshot is the data source; this widget does not
    /// walk the filesystem and does not invent a second tree model.
    /// </summary>
    public static GenericTreeNode FromFileTreeEntry(FileTreeEntry entry)
    {
        Ensure.NotNull(entry);
        ImmutableArray<FileTreeEntry> loaded = entry.ChildEntries;
        var children = new GenericTreeNode[loaded.Length];
        for (int i = 0; i < loaded.Length; i++)
        {
            children[i] = FromFileTreeEntry(loaded[i]);
        }

        return new GenericTreeNode(entry.Name, entry.FullPath, entry.IsDirectory, children, entry.IsHidden);
    }
}

/// <summary>
/// One visible row of a <see cref="GenericTreeView"/>: the node and its nesting
/// depth after applying expansion.
/// </summary>
public readonly record struct GenericTreeRow(GenericTreeNode Node, int Depth);

/// <summary>
/// Host-agnostic tree with a synchronous preview pane, generalized from the
/// chat file-tree panel (#2291). The tree is fed by a <see cref="FileTreeSnapshot"/>
/// that a host obtains through <c>IFileTreeLoader</c>, but this type knows no
/// panel id, store, message, or chat block. The host supplies a
/// <see cref="Preview"/> callback that turns the selected node into lines; the
/// view calls it inline while painting and performs no I/O of its own.
/// </summary>
/// <remarks>
/// <para>
///     Reuse of the existing file-tree pipeline is deliberate. A host calls
///     <c>IFileTreeLoader.Request</c> with its own id and directory, then feeds
///     the settled <c>FileTreeSnapshot</c> from its store into
///     <see cref="SetSnapshot"/>. The loader still owns the walk and its
///     cancellation; this widget only maps the result. No panel id, message,
///     store, or chat block is wired into the primitive, so it is not the
///     #2291 chat panel under another name.
/// </para>
/// <para>
///     Selection and expansion live here: a set of expanded node keys, a cursor
///     into the visible rows, and a scroll offset. Keys rather than node
///     references make expansion survive a snapshot refresh, which replaces the
///     node objects. The set is bounded by the snapshot's own node cap.
/// </para>
/// <para>
///     Key handling is a command gate (refuse Ctrl/Alt/Meta and Shift). Vim
///     <c>j</c>/<c>k</c>/<c>h</c>/<c>l</c> and the arrow keys move and toggle;
///     Enter and Space toggle a parent. A leaf's Enter is left to the host, the
///     same rule the chat tree uses. The file is declared as a command site in
///     the key-gate inventory.
/// </para>
/// </remarks>
public sealed class GenericTreeView
{
    private const int MinTreeWidth = 10;
    private const int MaxDepth = 64;

    private readonly List<GenericTreeNode> _roots = new();
    private readonly HashSet<string> _expanded = new(StringComparer.Ordinal);
    private readonly List<GenericTreeRow> _visible = new();

    private bool _dirty = true;
    private int _cursor;
    private int _offset;
    private int _lastHeight = 1;

    /// <summary>
    /// Synchronous preview producer for the selected node. Returned lines are
    /// painted in the right pane. Null or an empty result shows a placeholder.
    /// The callback is supplied by the host, so file reading stays outside the
    /// presentation layer.
    /// </summary>
    public Func<GenericTreeNode, IReadOnlyList<string>>? Preview { get; set; }

    /// <summary>Forest roots in display order.</summary>
    public IReadOnlyList<GenericTreeNode> Roots => _roots;

    /// <summary>Visible rows after expansion, rebuilt when the tree or expansion changes.</summary>
    public IReadOnlyList<GenericTreeRow> VisibleRows
    {
        get
        {
            EnsureVisible();
            return _visible;
        }
    }

    /// <summary>Number of visible rows.</summary>
    public int VisibleCount => VisibleRows.Count;

    /// <summary>Cursor position inside the visible rows, clamped.</summary>
    public int CursorIndex
    {
        get
        {
            EnsureVisible();
            return _visible.Count == 0 ? 0 : Math.Clamp(_cursor, 0, _visible.Count - 1);
        }
    }

    /// <summary>First visible row, for scrollbar sync and tests.</summary>
    public int Offset => _offset;

    /// <summary>Node under the cursor, or null for an empty tree.</summary>
    public GenericTreeNode? SelectedNode
    {
        get
        {
            EnsureVisible();
            return _visible.Count == 0 ? null : _visible[CursorIndex].Node;
        }
    }

    /// <summary>Raised on cursor, expansion, or content changes so a host can repaint.</summary>
    public event EventHandler? Changed;

    /// <summary>Replace the forest. Expansion keys survive; the cursor clamps to the new size.</summary>
    public void SetRoots(IReadOnlyList<GenericTreeNode> roots)
    {
        Ensure.NotNull(roots);
        _roots.Clear();
        for (int i = 0; i < roots.Count; i++)
        {
            _roots.Add(roots[i] ?? throw new ArgumentException("A root cannot be null.", nameof(roots)));
        }

        _dirty = true;
        ClampCursor();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Replace the forest with the entries of a settled file-tree snapshot.</summary>
    public void SetSnapshot(FileTreeSnapshot snapshot)
    {
        Ensure.NotNull(snapshot);
        ImmutableArray<FileTreeEntry> entries = snapshot.Entries;
        var roots = new GenericTreeNode[entries.Length];
        for (int i = 0; i < entries.Length; i++)
        {
            roots[i] = GenericTreeNode.FromFileTreeEntry(entries[i]);
        }

        SetRoots(roots);
    }

    /// <summary>Build a view over a settled file-tree snapshot.</summary>
    public static GenericTreeView FromSnapshot(FileTreeSnapshot snapshot)
    {
        var view = new GenericTreeView();
        view.SetSnapshot(snapshot);
        return view;
    }

    /// <summary>True when a parent node's children are visible.</summary>
    public bool IsExpanded(GenericTreeNode node) =>
        node.HasChildren && _expanded.Contains(node.Key);

    /// <summary>Flip a parent's expansion. Leaves stay collapsed. Returns the new state.</summary>
    public bool Toggle(GenericTreeNode node)
    {
        Ensure.NotNull(node);
        if (!node.HasChildren)
        {
            return false;
        }

        bool expanded = !_expanded.Contains(node.Key);
        if (expanded)
        {
            _expanded.Add(node.Key);
        }
        else
        {
            _expanded.Remove(node.Key);
        }

        _dirty = true;
        ClampCursor();
        Changed?.Invoke(this, EventArgs.Empty);
        return expanded;
    }

    /// <summary>Expand every parent in the forest.</summary>
    public void ExpandAll()
    {
        EnsureVisible();
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

    /// <summary>Collapse every node in the forest.</summary>
    public void CollapseAll()
    {
        if (_expanded.Count == 0)
        {
            return;
        }

        _expanded.Clear();
        _dirty = true;
        ClampCursor();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Route one key. Press and repeat only, with no modifiers. Arrows and vim
    /// keys move the cursor and toggle expansion, Enter and Space toggle a
    /// parent. Returns true when consumed.
    /// </summary>
    public bool HandleKey(in KeyEvent key)
    {
        if ((key.EventType != KeyEventType.Press && key.EventType != KeyEventType.Repeat)
            || !key.Modifiers.IsUnmodified())
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

    /// <summary>
    /// Paint the tree and its preview pane inside <paramref name="rect"/>. The
    /// left portion is the tree, a one-column divider follows it, the rest is
    /// the preview. Both portions clip to the rect.
    /// </summary>
    public void Paint(ScreenBuffer buffer, Rect rect)
    {
        Ensure.NotNull(buffer);
        if (rect.Width <= 0 || rect.Height <= 0)
        {
            return;
        }

        int treeWidth = Math.Max(MinTreeWidth, rect.Width * 2 / 5);
        if (rect.Width <= treeWidth + 2)
        {
            PaintTree(buffer, rect);
            return;
        }

        PaintTree(buffer, new Rect(rect.X, rect.Y, treeWidth, rect.Height));
        var dividerStyle = new CellStyle(ChatPalette.Border);
        for (int y = rect.Y; y < rect.Y + rect.Height; y++)
        {
            buffer.SetText(rect.X + treeWidth, y, "│".AsSpan(), dividerStyle);
        }

        int previewX = rect.X + treeWidth + 1;
        PaintPreview(buffer, new Rect(previewX, rect.Y, rect.Right - previewX, rect.Height));
    }

    /// <summary>Paint the visible tree rows only.</summary>
    public void PaintTree(ScreenBuffer buffer, Rect rect)
    {
        Ensure.NotNull(buffer);
        EnsureVisible();
        _lastHeight = Math.Max(1, rect.Height);
        if (rect.Width <= 0 || rect.Height <= 0)
        {
            return;
        }

        EnsureCursorInView();
        int rows = Math.Max(1, rect.Height);
        var rowStyle = new CellStyle(ChatPalette.Text);
        var selectedStyle = new CellStyle(ChatPalette.Text, attrs: StyleAttr.Reverse);
        var markerStyle = new CellStyle(ChatPalette.Accent, attrs: StyleAttr.Bold);
        CellStyle dimStyle = ChatPalette.Dim;

        for (int r = 0; r < rows; r++)
        {
            int index = _offset + r;
            if (index >= _visible.Count || r >= rect.Height)
            {
                break;
            }

            var (node, depth) = _visible[index];
            bool selected = index == CursorIndex;
            string glyph = node.IsDirectory
                ? (IsExpanded(node) ? "▾ " : "▸ ")
                : "· ";
            string marker = selected ? "> " : "  ";
            string text = marker + new string(' ', depth * 2) + glyph + node.Label;
            int width = Math.Max(0, rect.Width);
            buffer.SetText(rect.X, rect.Y + r, text.AsSpan(0, Math.Min(text.Length, width)), selected ? selectedStyle : rowStyle);
            if (selected && width > 0)
            {
                buffer.SetStyleAt(rect.X, rect.Y + r, markerStyle);
            }

            if (node.IsHidden && width > 1)
            {
                buffer.SetStyleAt(rect.X + Math.Min(width - 1, marker.Length + depth * 2), rect.Y + r, dimStyle);
            }
        }

        if (rect.Width >= 2 && _visible.Count > rows)
        {
            Scrollbar.TryPaint(
                buffer,
                new Rect(rect.X + rect.Width - 1, rect.Y, 1, Math.Min(rows, rect.Height)),
                _visible.Count,
                _offset,
                rows);
        }
    }

    /// <summary>
    /// Paint the preview lines for the selected node. The callback runs here,
    /// inline and synchronously, so the pane always matches the current cursor.
    /// </summary>
    public void PaintPreview(ScreenBuffer buffer, Rect rect)
    {
        Ensure.NotNull(buffer);
        if (rect.Width <= 0 || rect.Height <= 0)
        {
            return;
        }

        var titleStyle = new CellStyle(ChatPalette.Accent, attrs: StyleAttr.Bold);
        var bodyStyle = new CellStyle(ChatPalette.Text);
        CellStyle dimStyle = ChatPalette.Dim;

        GenericTreeNode? node = SelectedNode;
        string title = node is null ? "Preview" : "Preview: " + node.Label;
        int width = Math.Max(0, rect.Width);
        buffer.SetText(rect.X, rect.Y, title.AsSpan(0, Math.Min(title.Length, width)), titleStyle);

        IReadOnlyList<string>? lines = node is null || Preview is null ? null : Preview(node);
        if (lines is null || lines.Count == 0)
        {
            if (rect.Height > 1)
            {
                const string empty = "(no preview)";
                buffer.SetText(rect.X, rect.Y + 1, empty.AsSpan(0, Math.Min(empty.Length, width)), dimStyle);
            }

            return;
        }

        int y = rect.Y + 1;
        for (int i = 0; i < lines.Count && y < rect.Y + rect.Height; i++, y++)
        {
            string line = lines[i] ?? string.Empty;
            buffer.SetText(rect.X, y, line.AsSpan(0, Math.Min(line.Length, width)), bodyStyle);
        }
    }

    private bool HandleChar(Rune c)
    {
        return c.Value switch
        {
            'j' => Move(1),
            'k' => Move(-1),
            'h' => CollapseOrAscend(),
            'l' => ExpandOrDescend(),
            _ => false,
        };
    }

    private bool Move(int delta)
    {
        int target = Math.Clamp(_cursor + delta, 0, _visible.Count - 1);
        if (target == _cursor)
        {
            return true;
        }

        _cursor = target;
        EnsureCursorInView();
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
        EnsureCursorInView();
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    private bool ToggleAtCursor()
    {
        var node = _visible[CursorIndex].Node;
        return node.HasChildren && Toggle(node);
    }

    private bool ExpandOrDescend()
    {
        var node = _visible[CursorIndex].Node;
        if (!node.HasChildren)
        {
            return false;
        }

        if (!IsExpanded(node))
        {
            _ = Toggle(node);
            return true;
        }

        return MoveTo(CursorIndex + 1);
    }

    private bool CollapseOrAscend()
    {
        var (node, depth) = _visible[CursorIndex];
        if (node.HasChildren && IsExpanded(node))
        {
            _ = Toggle(node);
            return true;
        }

        for (int i = CursorIndex - 1; i >= 0; i--)
        {
            if (_visible[i].Depth == depth - 1)
            {
                return MoveTo(i);
            }
        }

        return false;
    }

    private bool ExpandDeep(GenericTreeNode node)
    {
        bool changed = node.HasChildren && !_expanded.Contains(node.Key);
        if (changed)
        {
            _expanded.Add(node.Key);
        }

        for (int i = 0; i < node.Children.Count; i++)
        {
            changed |= ExpandDeep(node.Children[i]);
        }

        return changed;
    }

    private void ClampCursor()
    {
        _dirty = true;
        EnsureVisible();
        _cursor = Math.Clamp(_cursor, 0, Math.Max(0, _visible.Count - 1));
        _offset = Math.Clamp(_offset, 0, Math.Max(0, _visible.Count - Math.Max(1, _lastHeight)));
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

        _cursor = Math.Clamp(_cursor, 0, Math.Max(0, _visible.Count - 1));
        EnsureCursorInView();
    }

    private void Append(GenericTreeNode node, int depth)
    {
        _visible.Add(new GenericTreeRow(node, depth));
        if (IsExpanded(node) && depth < MaxDepth)
        {
            for (int i = 0; i < node.Children.Count; i++)
            {
                Append(node.Children[i], depth + 1);
            }
        }
    }

    private void EnsureCursorInView()
    {
        int rows = Math.Max(1, _lastHeight);
        if (_cursor < _offset)
        {
            _offset = _cursor;
        }
        else if (_cursor >= _offset + rows)
        {
            _offset = _cursor - rows + 1;
        }
    }
}
