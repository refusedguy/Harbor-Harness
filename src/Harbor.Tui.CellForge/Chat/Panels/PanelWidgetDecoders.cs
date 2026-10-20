using Harbor.Ui.Framework.Panels;

namespace Harbor.Tui.CellForge.Panels;

/// <summary>
///     Priority-ordered widget decoder: tries one widget shape and flattens it
///     to CellForge text rows. Shaped after the RazorConsole translator sample
///     (30+ <c>IVdomElementTranslator</c>, priority-ordered, first match wins),
///     except the output is plain rows/cells — never a Spectre widget.
/// </summary>
/// <remarks>
///     Higher <see cref="Priority" /> runs first. Decoders run before the three
///     legacy <c>WidgetToRows</c> shapes (string / string-list / ToString
///     fallback), so a panel can return a richer widget (progress / table /
///     tree / form below) without breaking the text-rows contract.
/// </remarks>
public interface IPanelWidgetDecoder
{
    /// <summary>Higher wins; sample decoders below use 100.</summary>
    public int Priority { get; }

    /// <summary>
    ///     Try to flatten <paramref name="widget" /> to rows.
    ///     Return <see langword="false" /> (leaving <paramref name="rows" />
    ///     <see langword="null" />) when this decoder does not handle the shape.
    /// </summary>
    public bool TryDecode(object widget, PanelContext ctx, out IReadOnlyList<string>? rows);
}

/// <summary>Labeled fractional progress (0..1, clamped) → one bar row.</summary>
public sealed record PanelProgress(string Label, double Fraction);

/// <summary>Header + data rows → <c>a | b</c> rows with a separator line.</summary>
public sealed record PanelTable(IReadOnlyList<string> Headers, IReadOnlyList<IReadOnlyList<string>> Rows);

/// <summary>
///     Component of a panel tree: a labelled node that is either a leaf or a
///     composite. Leaf and composite are reached through this ONE interface,
///     so the code that walks a tree never branches on the kind of node it
///     stands on (#1521, Composite).
/// </summary>
public interface IPanelTreeNode
{
    /// <summary>The node's own text, without indentation.</summary>
    string Label { get; }

    /// <summary>
    ///     The node's children, in render order. Empty for a leaf.
    /// </summary>
    IReadOnlyList<IPanelTreeNode> Children { get; }
}

/// <summary>Leaf component of a panel tree: a node without children.</summary>
/// <param name="Label">The node's own text.</param>
public sealed record PanelTreeLeaf(string Label) : IPanelTreeNode
{
    /// <inheritdoc />
    public IReadOnlyList<IPanelTreeNode> Children { get; } = Array.Empty<IPanelTreeNode>();
}

/// <summary>
///     Composite component of a panel tree: a node that owns an ordered list
///     of children.
/// </summary>
public sealed record PanelTreeBranch : IPanelTreeNode
{
    /// <summary>Creates a branch over the given children.</summary>
    /// <param name="label">The node's own text.</param>
    /// <param name="children">
    ///     The children, in render order. Copied into the branch, so the tree
    ///     is an immutable snapshot: a list the caller keeps mutating cannot
    ///     change the tree under the decoder, and a cycle cannot be built
    ///     through it.
    /// </param>
    public PanelTreeBranch(string label, IEnumerable<IPanelTreeNode>? children = null)
    {
        Label = label;
        Children = children is null ? [] : [.. children];
    }

    /// <inheritdoc />
    public string Label { get; }

    /// <inheritdoc />
    public IReadOnlyList<IPanelTreeNode> Children { get; }
}

/// <summary>
///     Part-whole tree of panel nodes → one indented row per node. The
///     hierarchy lives in the structure (a parent owns its children), not in
///     a depth number next to a label (#1521).
/// </summary>
public sealed record PanelTree
{
    /// <summary>Creates a tree over the given roots.</summary>
    /// <param name="roots">
    ///     The top-level nodes, in render order. Copied into the tree, for
    ///     the same reason <see cref="PanelTreeBranch" /> copies its children.
    /// </param>
    public PanelTree(IEnumerable<IPanelTreeNode>? roots = null)
    {
        Roots = roots is null ? [] : [.. roots];
    }

    /// <summary>The top-level nodes, in render order.</summary>
    public IReadOnlyList<IPanelTreeNode> Roots { get; }
}

/// <summary>Titled form → title row plus <c>name: value</c> rows.</summary>
public sealed record PanelForm(string Title, IReadOnlyList<(string Name, string Value)> Fields);

/// <summary>Sample decoders for the four richer widgets (all priority 100).</summary>
public static class PanelWidgetDecoders
{
    public sealed class Progress : IPanelWidgetDecoder
    {
        public int Priority => 100;

        public bool TryDecode(object widget, PanelContext ctx, out IReadOnlyList<string>? rows)
        {
            if (widget is not PanelProgress p)
            {
                rows = null;
                return false;
            }

            double f = Math.Clamp(p.Fraction, 0, 1);
            int filled = (int)Math.Round(f * 16);
            rows = [$"{p.Label} [{new string('#', filled)}{new string('-', 16 - filled)}] {(int)Math.Round(f * 100)}%"];
            return true;
        }
    }

    public sealed class Table : IPanelWidgetDecoder
    {
        public int Priority => 100;

        public bool TryDecode(object widget, PanelContext ctx, out IReadOnlyList<string>? rows)
        {
            if (widget is not PanelTable t)
            {
                rows = null;
                return false;
            }

            var list = new List<string>(t.Rows.Count + 2) { string.Join(" | ", t.Headers) };
            list.Add(new string('-', Math.Max(list[0].Length, 3)));
            foreach (var r in t.Rows)
            {
                list.Add(string.Join(" | ", r));
            }

            rows = list;
            return true;
        }
    }

    /// <summary>
    ///     Tree widget → one indented row per node. The tree is walked as the
    ///     part-whole structure it is: the walk stands on a parent, resolves
    ///     the subtree of every child and hands that subtree down. No node
    ///     looks up its own children, and no node is matched by its type.
    /// </summary>
    public sealed class Tree : IPanelWidgetDecoder
    {
        /// <summary>Spaces of indentation per level of nesting.</summary>
        private const int IndentPerLevel = 2;

        public int Priority => 100;

        public bool TryDecode(object widget, PanelContext ctx, out IReadOnlyList<string>? rows)
        {
            if (widget is not PanelTree t)
            {
                rows = null;
                return false;
            }

            rows = Rows(t.Roots);
            return true;
        }

        /// <summary>
        ///     One walk over the part-whole tree: a parent before its
        ///     children, siblings in order. The walk is iterative, so a deep
        ///     chain cannot overflow the stack. That is the same reason
        ///     <c>TreeView</c>'s subscription walk is iterative.
        /// </summary>
        /// <param name="roots">The tree's top-level nodes.</param>
        /// <returns>One row per node, indented by its depth.</returns>
        private static IReadOnlyList<string> Rows(IReadOnlyList<IPanelTreeNode> roots)
        {
            var rows = new List<string>();
            var pending = new Stack<(IPanelTreeNode Node, int Depth)>();

            for (int i = roots.Count - 1; i >= 0; i--)
            {
                pending.Push((roots[i], 0));
            }

            while (pending.Count > 0)
            {
                (IPanelTreeNode node, int depth) = pending.Pop();
                rows.Add(new string(' ', depth * IndentPerLevel) + node.Label);

                IReadOnlyList<IPanelTreeNode> children = node.Children;
                for (int i = children.Count - 1; i >= 0; i--)
                {
                    pending.Push((children[i], depth + 1));
                }
            }

            return rows;
        }
    }

    public sealed class Form : IPanelWidgetDecoder
    {
        public int Priority => 100;

        public bool TryDecode(object widget, PanelContext ctx, out IReadOnlyList<string>? rows)
        {
            if (widget is not PanelForm f)
            {
                rows = null;
                return false;
            }

            var list = new List<string>(f.Fields.Count + 1) { f.Title };
            foreach (var field in f.Fields)
            {
                list.Add($"{field.Name}: {field.Value}");
            }

            rows = list;
            return true;
        }
    }
}
