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
    public SpecNode CreateNode(SpecKind kind, int sourceLine)
    {
        var node = new SpecNode(kind, sourceLine);
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
}
