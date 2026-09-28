using System.Collections.Frozen;
using Harbor.Registries.Tools;
using Microsoft.Extensions.Logging;
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
    // #358 (RESOLVED, §OOP-005): reads go through a single IToolSource path.
    // _frozenSource (when set by Freeze()) is the fast path; otherwise the
    // shared _concurrentSource reads the live ConcurrentDictionary. A third
    // source (e.g. lazy-loaded plugin tools) plugs in as another IToolSource
    // (or a composite) without touching GetAllTools / ResolveTools / GetTool.
    private readonly ConcurrentDictionary<ToolName, ITool> _tools = new();
    private readonly ConcurrentToolSource _concurrentSource;
    // #183: single volatile publish-once snapshot (frozen map + cached descriptor
    // arrays + per-ruleset filtered cache). One reference swap per Freeze(), so
    // readers never observe a torn view; dropping it drops the filtered cache too.
    private volatile IToolSource? _frozenSource;

    /// <summary>
    ///     Construct an empty registry reading from the live dictionary until
    ///     <see cref="Freeze" /> publishes a frozen snapshot.
    /// </summary>
    public ToolRegistry()
    {
        _concurrentSource = new ConcurrentToolSource(_tools, ToDescriptor);
    }

    /// <inheritdoc />
    public IReadOnlyList<ToolDescriptor> GetAllTools()
    {
        // Single path: one volatile read selects the active source; the cached
        // frozen array is returned as-is (zero alloc) when frozen.
        IToolSource source = _frozenSource ?? (IToolSource)_concurrentSource;
        return source.GetAllTools();
    }

    /// <inheritdoc />
    public IReadOnlyList<ToolDescriptor> ResolveTools(string agentName, PermissionRuleset? sessionPermission = null)
    {
        // Single path: frozen snapshot serves cached/memoized arrays (zero alloc
        // on repeat calls); unfrozen reads filter the live dictionary. Treat as read-only.
        IToolSource source = _frozenSource ?? (IToolSource)_concurrentSource;
        return source.ResolveTools(agentName, sessionPermission);
    }

    /// <inheritdoc />
    public Result<ITool> GetTool(ToolName name)
    {
        // Single path: frozen fast path first, live dictionary fallback inside
        // the source selection (no duplicated lookup logic here).
        IToolSource source = _frozenSource ?? (IToolSource)_concurrentSource;
        return source.GetTool(name);
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

    /// <summary>
    ///     Freeze the current tool set for fast lock-free lookups.
    ///     Call after all tools are registered at startup.
    /// </summary>
    public void Freeze()
    {
        // Atomic publish: readers observe either the prior snapshot or the new
        // one — never a half-built dictionary. The volatile write on
        // _frozenSource has release semantics so the frozen view is fully
        // visible before the reference is published.
        _frozenSource = FrozenToolView.Build(_tools.ToFrozenDictionary(), ToDescriptor);
    }

    private void InvalidateFrozenSnapshot()
    {
        // Lock-free invalidation: a single CAS-write publishes null. Concurrent
        // GetTool/ResolveTools readers may observe either the prior snapshot
        // (still valid — they just continue using the stale-but-consistent
        // frozen view until the next Freeze()) or null (and fall through to
        // the ConcurrentDictionary slow path). Both outcomes are safe. Dropping
        // the snapshot also drops its memoized per-ruleset arrays.
        Interlocked.Exchange(ref _frozenSource, (IToolSource?)null);
    }

    /// <summary>
    ///     Live-dictionary <see cref="IToolSource" />: the unfrozen read path.
    ///     Shares one instance per registry (holds the dictionary reference, no
    ///     per-call allocation for the source itself). Semantics mirror the
    ///     frozen view: agentName is ignored, a tool is included exactly when
    ///     <c>permission.Evaluate(name, "*")</c> allows it.
    /// </summary>
    private sealed class ConcurrentToolSource : IToolSource
    {
        private readonly ConcurrentDictionary<ToolName, ITool> _tools;
        private readonly Func<ITool, ToolDescriptor> _describe;

        public ConcurrentToolSource(ConcurrentDictionary<ToolName, ITool> tools, Func<ITool, ToolDescriptor> describe)
        {
            _tools = tools;
            _describe = describe;
        }

        public IReadOnlyList<ToolDescriptor> GetAllTools()
        {
            // Iterate concurrent dictionary directly (no intermediate array via ToArray()).
            int count = _tools.Count;
            if (count == 0)
            {
                return Array.Empty<ToolDescriptor>();
            }

            var list = new List<ToolDescriptor>(count);
            foreach (var t in _tools.Values)
            {
                list.Add(_describe(t));
            }
            return list;
        }

        public IReadOnlyList<ToolDescriptor> ResolveTools(string agentName, PermissionRuleset? sessionPermission = null)
        {
            var snapshot = _tools.Values;
            if (sessionPermission is null)
            {
                var result = new List<ToolDescriptor>(snapshot.Count);
                foreach (var t in snapshot)
                {
                    result.Add(_describe(t));
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
                        result.Add(_describe(t));
                    }
                }
                return result;
            }
        }

        public Result<ITool> GetTool(ToolName name)
        {
            if (_tools.TryGetValue(name, out var t))
            {
                return Result.Success(t);
            }

            return Result.Failure<ITool>($"Tool '{name}' is not registered.");
        }
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
    private readonly ILoggerFactory _loggerFactory;

    /// <summary>
    ///     Construct a builder backed by the supplied registry.
    /// </summary>
    /// <param name="registry">The registry to wrap.</param>
    /// <param name="loggerFactory">Logger factory for tool construction.</param>
    public ToolRegistryBuilder(IToolRegistry registry, ILoggerFactory loggerFactory)
    {
        _registry = registry;
        _loggerFactory = loggerFactory;
    }

    /// <inheritdoc />
    public void AddTool(ITool tool)
    {
        var result = _registry.Register(tool);
        if (result.IsFailure) // §4.6-ok: void-контракт, программная ошибка регистрации → исключение.
        {
            throw new InvalidOperationException(result.Error);
        }
    }

    /// <inheritdoc />
    public void AddTool<T>() where T : ITool, new() => AddTool(new T());

    /// <inheritdoc />
    public void AddTool(Func<ITool> factory) => AddTool(factory());

    /// <summary>
    ///     Register a tool via a factory interface.
    /// </summary>
    /// <param name="factory">The factory producing the tool instance.</param>
    public void AddTool(IToolFactory factory) => AddTool(factory.CreateTool(_loggerFactory));

    /// <inheritdoc />
    public void AddTool(Func<ILoggerFactory, ITool> factory) => AddTool(factory(_loggerFactory));
}
