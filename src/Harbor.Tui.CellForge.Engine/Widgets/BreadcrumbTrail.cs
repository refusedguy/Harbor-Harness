// #2324: generalized breadcrumb navigation history (k9s ctrl-g, broot, ranger,
// tmux). An ordered path of visited nodes with a movable current position and
// browser-style back/forward: push after going back drops the forward tail, so
// the history always records the path actually travelled. It is domain-agnostic:
// node type T and display text via labelOf let file trees, session drills and
// menus share one type. BCL-only and AOT-safe, render-thread owned like
// FocusStack; no extension axis, no Harbor imports (#555).

using System.Text;
using Harbor.Tui.CellForge.Rendering;

namespace Harbor.Tui.CellForge.Widgets;

/// <summary>One crumb of the active path, with its resolved display label.</summary>
/// <param name="Label">Display text for the node.</param>
/// <param name="IsCurrent">True for the node at <see cref="BreadcrumbTrail{T}.Position"/>.</param>
public readonly record struct BreadcrumbSegment(string Label, bool IsCurrent);

/// <summary>
/// Breadcrumb trail: a visited-node history with push/back/forward and three
/// render forms (plain text, per-crumb segments, direct buffer paint). Only the
/// active path (root through current) is rendered; the forward tail stays in
/// history and is reachable again with <see cref="Forward"/>.
/// </summary>
/// <typeparam name="T">Node payload; hosts map it to a label with <c>labelOf</c>.</typeparam>
public sealed class BreadcrumbTrail<T>
{
    private readonly List<T> _nodes = [];
    private readonly Func<T, string> _labelOf;
    private int _position = -1;

    /// <summary>Creates an empty trail over the given label selector.</summary>
    /// <param name="labelOf">Maps a node to its display text.</param>
    public BreadcrumbTrail(Func<T, string> labelOf)
    {
        _labelOf = labelOf ?? throw new ArgumentNullException(nameof(labelOf));
    }

    /// <summary>Nodes kept in history, including the forward tail.</summary>
    public int HistoryCount => _nodes.Count;

    /// <summary>Active path length: crumbs from root to current (0 when empty).</summary>
    public int Depth => _position + 1;

    /// <summary>Index of the current node in history, or -1 when empty.</summary>
    public int Position => _position;

    /// <summary>Current node, or <c>default(T)</c> when the trail is empty.</summary>
    public T? Current => _position >= 0 ? _nodes[_position] : default;

    /// <summary>Whether a node sits before the current one.</summary>
    public bool CanBack => _position > 0;

    /// <summary>Whether a node sits after the current one.</summary>
    public bool CanForward => _position >= 0 && _position < _nodes.Count - 1;

    /// <summary>Whether the trail holds no nodes.</summary>
    public bool IsEmpty => _nodes.Count == 0;

    /// <summary>
    /// Appends a node as the newest current crumb. A forward tail left by an
    /// earlier <see cref="Back"/> is dropped first (branch pruning): pushing
    /// after going back rewrites the future, as browser history does. Returns
    /// the new <see cref="Depth"/>.
    /// </summary>
    public int Push(T node)
    {
        if (CanForward)
        {
            _nodes.RemoveRange(_position + 1, _nodes.Count - _position - 1);
        }

        _nodes.Add(node);
        _position = _nodes.Count - 1;
        return _position + 1;
    }

    /// <summary>Moves one crumb toward the root. Returns false at the head.</summary>
    public bool Back()
    {
        if (!CanBack)
        {
            return false;
        }

        _position--;
        return true;
    }

    /// <summary>Moves one crumb toward the newest visited node. Returns false at the tail.</summary>
    public bool Forward()
    {
        if (!CanForward)
        {
            return false;
        }

        _position++;
        return true;
    }

    /// <summary>Active path nodes, root first and current last. Fresh list per call.</summary>
    public IReadOnlyList<T> ActivePath()
    {
        var path = new List<T>(_position + 1);
        for (int i = 0; i <= _position; i++)
        {
            path.Add(_nodes[i]);
        }

        return path;
    }

    /// <summary>Active path as resolved segments, root first and current last.</summary>
    public IReadOnlyList<BreadcrumbSegment> Segments()
    {
        var segments = new List<BreadcrumbSegment>(_position + 1);
        for (int i = 0; i <= _position; i++)
        {
            segments.Add(new BreadcrumbSegment(_labelOf(_nodes[i]), i == _position));
        }

        return segments;
    }

    /// <summary>Plain-text breadcrumb: active labels joined by <paramref name="separator"/>.</summary>
    public string Render(string separator = " / ")
    {
        var text = new StringBuilder();
        for (int i = 0; i <= _position; i++)
        {
            if (i > 0)
            {
                text.Append(separator);
            }

            text.Append(_labelOf(_nodes[i]));
        }

        return text.ToString();
    }

    /// <summary>Cell width of <see cref="Render"/> (Unicode-aware) without building the string.</summary>
    public int MeasureWidth(string separator = " / ")
    {
        int width = 0;
        int separatorWidth = UnicodeWidth.Width(separator.AsSpan());
        for (int i = 0; i <= _position; i++)
        {
            if (i > 0)
            {
                width += separatorWidth;
            }

            width += UnicodeWidth.Width(_labelOf(_nodes[i]).AsSpan());
        }

        return width;
    }

    /// <summary>
    /// Paints the active path on one row through <see cref="MarkupCells.Paint"/>:
    /// ancestors dim, current bold. Markup escaping, wide runes and clipping
    /// behave like every other CellForge label. Returns the painted width.
    /// </summary>
    public int Paint(ScreenBuffer buffer, int x, int y, string separator = " / ", CellStyle style = default)
    {
        var markup = new StringBuilder();
        for (int i = 0; i <= _position; i++)
        {
            if (i > 0)
            {
                markup.Append(MarkupCells.Escape(separator));
            }

            markup.Append(i == _position ? "[bold]" : "[dim]")
                .Append(MarkupCells.Escape(_labelOf(_nodes[i])))
                .Append("[/]");
        }

        return MarkupCells.Paint(buffer, x, y, markup.ToString(), style);
    }
}
