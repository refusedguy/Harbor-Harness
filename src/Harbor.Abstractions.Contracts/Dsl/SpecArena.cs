namespace Harbor.Abstractions.Dsl;

/// <summary>
///     Owns every node produced by one parse: all nodes in creation order and
///     the roots of the tree. Issue #3083 also lets one arena carry the
///     <c>.harbor-ui</c> module graph, so imported specs are reachable by
///     their stable module name and cycles can be diagnosed without Roslyn.
/// </summary>
public sealed class SpecArena
{
    private readonly List<SpecNode> _nodes = [];
    private readonly List<SpecNode> _roots = [];
    private readonly List<SpecModule> _modules = [];

    /// <summary>Creates an empty arena.</summary>
    public SpecArena()
    {
    }

    /// <summary>All nodes in creation order.</summary>
    public IReadOnlyList<SpecNode> Nodes => _nodes;

    /// <summary>Nodes without a parent, in source order.</summary>
    public IReadOnlyList<SpecNode> Roots => _roots;

    /// <summary>Modules of the graph this arena owns, in stable name order.</summary>
    internal IReadOnlyList<SpecModule> Modules => _modules;

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

    /// <summary>Adds one module to the graph owned by this arena.</summary>
    /// <param name="module">Module to add.</param>
    internal void AddModule(SpecModule module)
    {
        ArgumentNullException.ThrowIfNull(module);
        _modules.Add(module);
    }

    /// <summary>Finds a module by its stable name, or null when it is not in the graph.</summary>
    /// <param name="path">Normalized module name.</param>
    internal SpecModule? FindModule(string path)
    {
        foreach (var module in _modules)
        {
            if (string.Equals(module.Path, path, StringComparison.Ordinal))
                return module;
        }

        return null;
    }

    /// <summary>
    ///     Walks the import graph depth-first and returns one diagnostic per
    ///     back edge. The walk is deterministic: modules and imports are
    ///     visited in ordinal name and source order, and a module that was
    ///     fully visited is not walked again. A missing target is ignored here;
    ///     the resolver reports it separately.
    /// </summary>
    internal IReadOnlyList<SpecImportCycle> DetectImportCycles()
    {
        var byPath = new Dictionary<string, SpecModule>(StringComparer.Ordinal);
        foreach (var module in _modules)
            byPath[module.Path] = module;

        var ordered = new List<SpecModule>(_modules);
        ordered.Sort(static (left, right) => string.CompareOrdinal(left.Path, right.Path));

        var state = new Dictionary<string, int>(StringComparer.Ordinal);
        var stack = new List<string>();
        var cycles = new List<SpecImportCycle>();

        foreach (var module in ordered)
            Visit(module, byPath, state, stack, cycles);

        return cycles;
    }

    private static void Visit(
        SpecModule module,
        Dictionary<string, SpecModule> byPath,
        Dictionary<string, int> state,
        List<string> stack,
        List<SpecImportCycle> cycles)
    {
        if (state.TryGetValue(module.Path, out var mark) && mark != 0)
            return;

        state[module.Path] = 1;
        stack.Add(module.Path);
        foreach (var import in module.Imports)
        {
            if (!byPath.TryGetValue(import.Path, out var target))
                continue;

            if (state.TryGetValue(target.Path, out var targetMark) && targetMark == 1)
            {
                var start = stack.IndexOf(target.Path);
                var chain = new List<string>();
                for (var index = start; index < stack.Count; index++)
                    chain.Add(stack[index]);
                chain.Add(target.Path);
                cycles.Add(new SpecImportCycle(
                    chain,
                    import.Path,
                    import.Line,
                    import.Column,
                    "Cyclic import: " + string.Join(" -> ", chain)));
                continue;
            }

            Visit(target, byPath, state, stack, cycles);
        }

        stack.RemoveAt(stack.Count - 1);
        state[module.Path] = 2;
    }

    /// <summary>
    ///     Renders every root in source order as one deterministic text tree.
    ///     The output depends only on the IR, so it can be committed as a
    ///     snapshot without starting a renderer.
    /// </summary>
    public string DebugTree() => SpecTreeDumper.DebugTree(this);
}
