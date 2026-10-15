using System.Collections.Frozen;

namespace Harbor.Registries.Tools;

/// <summary>
///     Immutable publish-once snapshot of a frozen tool set (#183).
///     Built once per <c>Freeze()</c> and swapped in with a single volatile
///     write, so readers never observe a torn view — a frozen map from one
///     generation paired with a descriptor array from another.
/// </summary>
/// <remarks>
///     <para>
///         The unfiltered descriptor array is built at freeze time, sorted by
///         name (ordinal), and returned as-is: repeat <c>ResolveTools</c> /
///         <c>GetAllTools</c> calls allocate nothing and the per-turn consumer
///         iterates without sorting. Per-ruleset filtered arrays are memoized on first use, so the
///         per-turn hot path (the same <c>agent.Permission</c> instance every turn)
///         also resolves without allocating. Filtered entries are keyed by ruleset
///         reference — a fresh ruleset instance computes once, then hits the cache.
///     </para>
///     <para>
///         Callers must treat returned lists as read-only snapshots and never mutate
///         them. Dropping the snapshot (on register/unregister) drops its filtered
///         cache with it, so stale entries cannot survive invalidation.
///     </para>
/// </remarks>
internal sealed class FrozenToolView : IToolSource
{
    private readonly FrozenDictionary<ToolName, ITool> _tools;
    private readonly ToolDescriptor[] _all;
    private readonly Dictionary<PermissionRuleset, ToolDescriptor[]> _byPermission = new(ReferenceEqualityComparer.Instance);
    private readonly object _gate = new();

    private FrozenToolView(
        FrozenDictionary<ToolName, ITool> tools,
        ToolDescriptor[] all)
    {
        _tools = tools;
        _all = all;
    }

    /// <summary>
    ///     Build a snapshot from an already-frozen tool map.
    /// </summary>
    /// <param name="tools">The frozen tool map.</param>
    /// <param name="describe">Descriptor factory (no LINQ — plain loop).</param>
    /// <remarks>
    ///     #1240: the array is sorted by name (ordinal) at freeze time, so the
    ///     per-turn consumer (<c>SystemPromptBuilder</c>) iterates it as-is
    ///     instead of paying an <c>OrderBy</c> per turn. Freeze is a
    ///     startup/reload event, never the hot path.
    /// </remarks>
    public static FrozenToolView Build(
        FrozenDictionary<ToolName, ITool> tools,
        Func<ITool, ToolDescriptor> describe)
    {
        var all = new ToolDescriptor[tools.Count];
        int i = 0;
        foreach (var kv in tools)
        {
            all[i++] = describe(kv.Value);
        }

        Array.Sort(all, CompareByName);
        return new FrozenToolView(tools, all);
    }

    private static int CompareByName(ToolDescriptor left, ToolDescriptor right) =>
        string.CompareOrdinal(left.Name.Value, right.Name.Value);

    /// <summary>
    ///     All descriptors in this snapshot. The cached array is returned as-is (zero alloc).
    /// </summary>
    public IReadOnlyList<ToolDescriptor> GetAll() => _all;

    /// <summary>
    ///     Resolve descriptors, filtered by <paramref name="permission" /> when given.
    ///     The unfiltered array and every per-ruleset filtered array are cached: repeat
    ///     calls with the same ruleset instance return the cached array with zero
    ///     allocations. A tool is included exactly when
    ///     <c>permission.Evaluate(name, "*")</c> allows it — evaluation semantics
    ///     are unchanged.
    /// </summary>
    /// <remarks>
    ///     #1100: describe-once — the filter reuses the prebuilt <c>_all</c>
    ///     descriptor instances instead of re-describing each allowed tool, so a
    ///     first-time filtered resolve pays N evaluates + one array, not N
    ///     describes + a re-sort. Filtering a sorted array preserves the order,
    ///     so no re-sort is needed. An allow-all ruleset shares the <c>_all</c>
    ///     instance itself (callers treat snapshots as read-only).
    /// </remarks>
    public IReadOnlyList<ToolDescriptor> Resolve(PermissionRuleset? permission)
    {
        if (permission is null)
        {
            return _all;
        }

        lock (_gate)
        {
            if (_byPermission.TryGetValue(permission, out var cached))
            {
                return cached;
            }
        }

        // Compute outside the lock: Evaluate is pure with respect to this snapshot,
        // and holding the gate across N evaluations would serialize concurrent
        // first-time resolves. Filter over the prebuilt descriptors — no
        // re-describe, order preserved, no re-sort.
        var all = _all;
        var buf = new ToolDescriptor[all.Length];
        int n = 0;
        for (int i = 0; i < all.Length; i++)
        {
            var d = all[i];
            if (permission.Evaluate(d.Name.Value, "*") == PermissionAction.Allow)
            {
                buf[n++] = d;
            }
        }

        ToolDescriptor[] snapshot;
        if (n == 0)
        {
            snapshot = Array.Empty<ToolDescriptor>();
        }
        else if (n == buf.Length)
        {
            snapshot = all;
        }
        else
        {
            snapshot = new ToolDescriptor[n];
            Array.Copy(buf, snapshot, n);
        }

        lock (_gate)
        {
            if (_byPermission.TryGetValue(permission, out var raced))
            {
                return raced;
            }

            _byPermission[permission] = snapshot;
            return snapshot;
        }
    }

    public bool TryGetTool(ToolName name, out ITool? tool) => _tools.TryGetValue(name, out tool);

    // IToolSource: single-path delegation target for ToolRegistry (#358).
    // agentName is intentionally ignored — permission filtering uses only the
    // session ruleset, identical to ToolRegistry.ResolveTools semantics.
    public IReadOnlyList<ToolDescriptor> GetAllTools() => GetAll();

    public IReadOnlyList<ToolDescriptor> ResolveTools(string agentName, PermissionRuleset? sessionPermission = null)
        => Resolve(sessionPermission);

    public Result<ITool> GetTool(ToolName name)
        => TryGetTool(name, out var tool) && tool is not null
            ? Result.Success(tool)
            : Result.Failure<ITool>($"Tool '{name}' is not registered.");
}
