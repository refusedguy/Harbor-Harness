using System.Collections.Frozen;
using NonBlocking;
namespace Harbor.Abstractions.Tools;
/// <summary>
///     Thread-safe tool registry with frozen lookup table for fast resolution.
///     Implements Registry pattern (GOF).
///     Hot path: <see cref="ResolveTools" /> / <see cref="GetTool" /> — uses frozen snapshot when available.
///     Backing storage is <see cref="NonBlocking.ConcurrentDictionary{TKey, TValue}" /> for lock-free
///     scaling under write-heavy workloads.
/// </summary>
public sealed class ToolRegistry : IToolRegistry
{
    // Architecture audit v2 §CONCURRENCY-001 (RESOLVED): InvalidateFrozenSnapshot
    // previously took `lock(_frozenLock)` just to set a single reference field to
    // null — under write-heavy load (e.g. plugins registering many tools at
    // startup, or hot-reload of plugin sources) this serialised every Register /
    // Unregister call and caused a thundering-herd: each InvalidateFrozenSnapshot
    // wake-up forced every concurrent GetTool/ResolveTools through the slow
    // ConcurrentDictionary fallback while the lock was held. The fix is a single
    // Interlocked.Exchange on a volatile field — no lock, no contention, and the
    // CAS guarantees the next read sees null (volatile-read acquire) so the slow
    // path is taken at most once per invalidation cycle.
    //
    // TODO(principles)[OCP, ROP]: двойной путь — frozen vs concurrent — дублирует
    // логику в GetAllTools / ResolveTools / GetTool. Если добавить третий источник
    // (например, lazy-loaded tools из плагинов), придётся ещё раз дублировать.
    // Лучше — CompositeToolRegistry, делегирующий в один из IToolSource. См. §OOP-005.
    private readonly ConcurrentDictionary<ToolName, ITool> _tools = new();
    private volatile FrozenDictionary<ToolName, ITool>? _frozenTools;

    /// <inheritdoc />
    public IReadOnlyList<ToolDescriptor> GetAllTools()
    {
        // Prefer frozen snapshot (lock-free, smaller memory footprint).
        var frozen = _frozenTools;
        if (frozen is not null)
        {
            var result = new ToolDescriptor[frozen.Count];
            int i = 0;
            foreach (var kv in frozen)
            {
                result[i++] = ToDescriptor(kv.Value);
            }
            return result;
        }

        // Fallback: iterate concurrent dictionary directly (no intermediate array via ToArray()).
        int count = _tools.Count;
        if (count == 0)
        {
            return Array.Empty<ToolDescriptor>();
        }

        var list = new List<ToolDescriptor>(count);
        foreach (var t in _tools.Values)
        {
            list.Add(ToDescriptor(t));
        }
        return list;
    }

    /// <inheritdoc />
    public IReadOnlyList<ToolDescriptor> ResolveTools(string agentName, PermissionRuleset? sessionPermission = null)
    {
        var frozen = _frozenTools;
        if (frozen is not null)
        {
            return sessionPermission is null
                ? ResolveAllFromFrozen(frozen)
                : ResolveFilteredFromFrozen(frozen, sessionPermission);
        }

        var snapshot = _tools.Values;
        if (sessionPermission is null)
        {
            var result = new List<ToolDescriptor>(snapshot.Count);
            foreach (var t in snapshot)
            {
                result.Add(ToDescriptor(t));
            }
            return result;
        }
        else
        {
            var result = new List<ToolDescriptor>(snapshot.Count);
            foreach (var t in snapshot)
            {
                if (sessionPermission.Evaluate(t.Name.Value, "*") == PermissionAction.Allow)
                {
                    result.Add(ToDescriptor(t));
                }
            }
            return result;
        }
    }

    /// <inheritdoc />
    public Result<ITool> GetTool(ToolName name)
    {
        // Try frozen snapshot first (fast path)
        var frozen = _frozenTools;
        if (frozen is not null && frozen.TryGetValue(name, out var tool))
        {
            return Result.Success(tool);
        }

        // Fallback to concurrent dictionary
        if (_tools.TryGetValue(name, out var t))
        {
            return Result.Success(t);
        }

        return Result.Failure<ITool>($"Tool '{name}' is not registered.");
    }

    /// <inheritdoc />
    public Result Register(ITool tool)
    {
        if (!_tools.TryAdd(tool.Name, tool))
        {
            return Result.Failure($"Tool '{tool.Name}' is already registered.");
        }

        InvalidateFrozenSnapshot();
        return Result.Success();
    }

    /// <inheritdoc />
    public Result Unregister(ToolName name)
    {
        if (_tools.TryRemove(name, out _))
        {
            InvalidateFrozenSnapshot();
            return Result.Success();
        }

        return Result.Failure($"Tool '{name}' is not registered.");
    }

    private static ToolDescriptor[] ResolveAllFromFrozen(FrozenDictionary<ToolName, ITool> frozen)
    {
        var result = new ToolDescriptor[frozen.Count];
        int i = 0;
        foreach (var kv in frozen)
        {
            result[i++] = ToDescriptor(kv.Value);
        }
        return result;
    }

    private static List<ToolDescriptor> ResolveFilteredFromFrozen(
        FrozenDictionary<ToolName, ITool> frozen,
        PermissionRuleset sessionPermission)
    {
        // Upper-bound the capacity; filtering happens after.
        var result = new List<ToolDescriptor>(frozen.Count);
        foreach (var t in frozen.Values)
        {
            if (sessionPermission.Evaluate(t.Name.Value, "*") == PermissionAction.Allow)
            {
                result.Add(ToDescriptor(t));
            }
        }
        return result;
    }

    /// <summary>
    ///     Freeze the current tool set for fast lock-free lookups.
    ///     Call after all tools are registered at startup.
    /// </summary>
    public void Freeze()
    {
        // Atomic publish: readers observe either the prior snapshot or the new
        // one — never a half-built dictionary. The volatile write on
        // _frozenTools has release semantics so the frozen dictionary is fully
        // visible before the reference is published.
        _frozenTools = _tools.ToFrozenDictionary();
    }

    private void InvalidateFrozenSnapshot()
    {
        // Lock-free invalidation: a single CAS-write publishes null. Concurrent
        // GetTool/ResolveTools readers may observe either the prior snapshot
        // (still valid — they just continue using the stale-but-consistent
        // frozen view until the next Freeze()) or null (and fall through to
        // the ConcurrentDictionary slow path). Both outcomes are safe.
        Interlocked.Exchange(ref _frozenTools, null);
    }

    private static ToolDescriptor ToDescriptor(ITool t) => new(
        t.Name,
        t.DisplayName,
        t.Description,
        t.ParameterSchema,
        t.ExecutionMode,
        t.PromptSnippet,
        t.PromptGuidelines);
}

/// <summary>
///     Builder implementation for <see cref="IToolRegistryBuilder" />.
/// </summary>
public sealed class ToolRegistryBuilder : IToolRegistryBuilder
{
    private readonly IToolRegistry _registry;

    /// <summary>
    ///     Construct a builder backed by the supplied registry.
    /// </summary>
    /// <param name="registry">The registry to wrap.</param>
    public ToolRegistryBuilder(IToolRegistry registry)
    {
        _registry = registry;
    }

    /// <inheritdoc />
    public void AddTool(ITool tool)
    {
        var result = _registry.Register(tool);
        if (result.IsFailure)
        {
            throw new InvalidOperationException(result.Error);
        }
    }

    /// <inheritdoc />
    public void AddTool<T>() where T : ITool, new() => AddTool(new T());

    /// <inheritdoc />
    public void AddTool(Func<ITool> factory) => AddTool(factory());
}
