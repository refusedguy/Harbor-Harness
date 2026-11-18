namespace Harbor.Abstractions.Dsl;

/// <summary>
///     Owns every node produced by one parse: all nodes in creation order and
///     the roots of the tree.
/// </summary>
public sealed class SpecArena
{
    private readonly List<SpecNode> _nodes = [];
    private readonly List<SpecNode> _roots = [];

    /// <summary>Creates an empty arena.</summary>
    public SpecArena()
    {
    }

    /// <summary>All nodes in creation order.</summary>
    public IReadOnlyList<SpecNode> Nodes => _nodes;

    /// <summary>Nodes without a parent, in source order.</summary>
    public IReadOnlyList<SpecNode> Roots => _roots;

    /// <summary>Creates a node and records it in this arena.</summary>
    /// <param name="kind">Component kind.</param>
    /// <param name="sourceLine">Line in the markup file, one-based.</param>
    /// <remarks>The source column is unknown and recorded as 1.</remarks>
    public SpecNode CreateNode(SpecKind kind, int sourceLine) =>
        CreateNode(kind, sourceLine, 1);

    /// <summary>Creates a node with a 1-based source span and records it in this arena.</summary>
    /// <param name="kind">Component kind.</param>
    /// <param name="sourceLine">Line in the markup file, one-based.</param>
    /// <param name="sourceColumn">Column of the kind identifier, one-based.</param>
    public SpecNode CreateNode(SpecKind kind, int sourceLine, int sourceColumn)
    {
        var node = new SpecNode(kind, sourceLine, sourceColumn);
        _nodes.Add(node);
        return node;
    }

    /// <summary>Adds a root node.</summary>
    /// <param name="node">Node to add.</param>
    /// <exception cref="SpecNodeReuseException">
    ///     The node already has a parent or was claimed.
    /// </exception>
    public void AddRoot(SpecNode node)
    {
        if (node is null)
            throw new ArgumentNullException(nameof(node));
        if (node.Parent is not null)
            throw new SpecNodeReuseException(
                $"SpecNode {node.Kind} at line {node.SourceLine} already has a parent; a node is single-use.");
        if (node.IsClaimed)
            throw new SpecNodeReuseException(
                $"SpecNode {node.Kind} at line {node.SourceLine} was already claimed; a node is single-use.");

        node.MarkParented();
        _roots.Add(node);
    }

    /// <summary>
    ///     Renders every root in source order as one deterministic text tree.
    ///     The output depends only on the IR, so it can be committed as a
    ///     snapshot without starting a renderer.
    /// </summary>
    public string DebugTree() => SpecTreeDumper.DebugTree(this);
}
