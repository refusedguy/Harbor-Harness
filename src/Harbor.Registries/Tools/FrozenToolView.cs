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
///         The unfiltered descriptor array is built at freeze time and returned
///         as-is: repeat <c>ResolveTools</c> / <c>GetAllTools</c> calls allocate
///         nothing. Per-ruleset filtered arrays are memoized on first use, so the
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
internal sealed class FrozenToolView
{
    private readonly FrozenDictionary<ToolName, ITool> _tools;
    private readonly ToolDescriptor[] _all;
    private readonly Func<ITool, ToolDescriptor> _describe;
    private readonly Dictionary<PermissionRuleset, ToolDescriptor[]> _byPermission = new(ReferenceEqualityComparer.Instance);
    private readonly object _gate = new();

    private FrozenToolView(
        FrozenDictionary<ToolName, ITool> tools,
        ToolDescriptor[] all,
        Func<ITool, ToolDescriptor> describe)
    {
        _tools = tools;
        _all = all;
        _describe = describe;
    }

    /// <summary>
    ///     Build a snapshot from an already-frozen tool map.
    /// </summary>
    /// <param name="tools">The frozen tool map.</param>
    /// <param name="describe">Descriptor factory (no LINQ — plain loop).</param>
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

        return new FrozenToolView(tools, all, describe);
    }

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
        // first-time resolves.
        var buf = new ToolDescriptor[_tools.Count];
        int n = 0;
        foreach (var t in _tools.Values)
        {
            if (permission.Evaluate(t.Name.Value, "*") == PermissionAction.Allow)
            {
                buf[n++] = _describe(t);
            }
        }

        ToolDescriptor[] snapshot;
        if (n == 0)
        {
            snapshot = Array.Empty<ToolDescriptor>();
        }
        else if (n == buf.Length)
        {
            snapshot = buf;
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
}
