namespace Harbor.Abstractions.Dsl;

/// <summary>
///     One node of the parsed markup tree: a kind, recorded operations and
///     child nodes.
/// </summary>
/// <remarks>
///     Every node has single-use semantics. It may be attached to one parent
///     and claimed by one consumer. Both violations throw
///     <see cref="SpecNodeReuseException"/> with the node kind and source line
///     in the message.
/// </remarks>
public sealed class SpecNode
{
    private readonly List<SpecOp> _operations = [];
    private readonly List<SpecNode> _children = [];
    private bool _claimed;

    /// <summary>Creates a node for a kind and a 1-based source line.</summary>
    /// <param name="kind">Component kind.</param>
    /// <param name="sourceLine">Line in the markup file, one-based.</param>
    /// <remarks>The source column is unknown and recorded as 1.</remarks>
    public SpecNode(SpecKind kind, int sourceLine) : this(kind, sourceLine, 1)
    {
    }

    /// <summary>Creates a node for a kind and a 1-based source span.</summary>
    /// <param name="kind">Component kind.</param>
    /// <param name="sourceLine">Line in the markup file, one-based.</param>
    /// <param name="sourceColumn">Column of the kind identifier, one-based.</param>
    public SpecNode(SpecKind kind, int sourceLine, int sourceColumn)
    {
        Kind = kind;
        SourceLine = sourceLine;
        SourceColumn = sourceColumn;
    }

    /// <summary>Component kind.</summary>
    public SpecKind Kind { get; }

    /// <summary>Line in the markup file, one-based.</summary>
    public int SourceLine { get; }

    /// <summary>Column of the kind identifier in the markup file, one-based.</summary>
    public int SourceColumn { get; }

    /// <summary>Recorded operations in source order.</summary>
    public IReadOnlyList<SpecOp> Operations => _operations;

    /// <summary>Child nodes in source order.</summary>
    public IReadOnlyList<SpecNode> Children => _children;

    /// <summary>Whether a consumer has claimed this node.</summary>
    public bool IsClaimed => _claimed;

    internal SpecNode? Parent { get; private set; }

    /// <summary>Appends an operation to this node.</summary>
    /// <param name="op">Operation to record.</param>
    /// <exception cref="SpecNodeReuseException">The node was already claimed.</exception>
    public void AddOperation(SpecOp op)
    {
        if (op is null)
            throw new ArgumentNullException(nameof(op));
        if (_claimed)
            throw Reuse("cannot receive an operation after it was claimed");

        _operations.Add(op);
    }

    /// <summary>Attaches a child node to this node.</summary>
    /// <param name="child">Child to attach.</param>
    /// <exception cref="SpecNodeReuseException">
    ///     The child already has a parent or was claimed, or this node was claimed.
    /// </exception>
    public void AddChild(SpecNode child)
    {
        if (child is null)
            throw new ArgumentNullException(nameof(child));
        if (_claimed)
            throw Reuse("cannot receive a child after it was claimed");
        if (child.Parent is not null)
            throw child.Reuse("already has a parent");
        if (child._claimed)
            throw child.Reuse("was already claimed");

        child.Parent = this;
        _children.Add(child);
    }

    /// <summary>
    ///     Claims the node for one consumer. A second claim throws instead of
    ///     letting two consumers mutate the same tree.
    /// </summary>
    /// <exception cref="SpecNodeReuseException">The node was already claimed.</exception>
    public void Claim()
    {
        if (_claimed)
            throw Reuse("was already claimed");

        _claimed = true;
    }

    /// <summary>
    ///     Renders this subtree as a deterministic text tree. No renderer or
    ///     runtime state is involved, so the result is stable enough for a
    ///     committed snapshot.
    /// </summary>
    public string DebugTree() => SpecTreeDumper.DebugTree(this);

    internal void MarkParented() => Parent = this;

    private SpecNodeReuseException Reuse(string reason) =>
        new($"SpecNode {Kind} at line {SourceLine} {reason}; a node is single-use.");
}
