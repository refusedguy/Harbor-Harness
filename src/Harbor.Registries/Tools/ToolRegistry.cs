using System.Collections.Frozen;
using Microsoft.Extensions.Logging;
using NonBlocking;
namespace Harbor.Registries.Tools;
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
    // used to take a lock just to publish a null reference. Under write-heavy load
    // (plugins registering many tools, hot-reload of plugin sources) that
    // serialised every Register / Unregister call. Publication is lock-free now.
    //
    // #2570: the frozen source and the guard set derived from the registered
    // tools (#557) are one immutable snapshot, published with a single volatile
    // write. A reader sees either (old source, old guards) or (new source, new
    // guards), never a registered path-taking tool whose guard is missing. Two
    // separate volatile writes could be observed torn; one reference swap cannot.
    //
    // #358 (RESOLVED, §OOP-005): reads go through a single IToolSource path.
    // Snapshot.FrozenSource (when set by Freeze()) is the fast path; otherwise
    // the shared _concurrentSource reads the live ConcurrentDictionary. A third
    // source (e.g. lazy-loaded plugin tools) plugs in as another IToolSource
    // (or a composite) without touching GetAllTools / ResolveTools / GetTool.
    private readonly ConcurrentDictionary<ToolName, ITool> _tools = new();
    private readonly ConcurrentToolSource _concurrentSource;

    /// <summary>
    ///     One publication unit: the active source and the safety policies
    ///     derived from the tool set it was built from (#2570).
    /// </summary>
    private sealed record RegistrySnapshot(
        IToolSource? FrozenSource,
        IReadOnlyList<IArgSafetyPolicy> SafetyPolicies)
    {
        public static RegistrySnapshot Empty { get; } = new(null, ToolSafetyPolicies.Build([]));
    }

    // #183: publish-once snapshot (frozen map + cached descriptor arrays + per-
    // ruleset filtered cache). Dropping it drops the filtered cache too.
    private volatile RegistrySnapshot _snapshot = RegistrySnapshot.Empty;

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
        // One volatile read selects source and guards as a pair (#2570); the
        // cached frozen array is returned as-is (zero alloc) when frozen.
        RegistrySnapshot snapshot = _snapshot;
        IToolSource source = snapshot.FrozenSource ?? (IToolSource)_concurrentSource;
        return source.GetAllTools();
    }

    /// <inheritdoc />
    public IReadOnlyList<ToolDescriptor> ResolveTools(string agentName, PermissionRuleset? sessionPermission = null)
    {
        // Frozen snapshot serves cached/memoized arrays (zero alloc on repeat
        // calls); unfrozen reads filter the live dictionary. Read-only result.
        RegistrySnapshot snapshot = _snapshot;
        IToolSource source = snapshot.FrozenSource ?? (IToolSource)_concurrentSource;
        return source.ResolveTools(agentName, sessionPermission);
    }

    /// <inheritdoc />
    public Result<ITool> GetTool(ToolName name)
    {
        // Frozen fast path first, live dictionary fallback inside the source
        // selection (no duplicated lookup logic here).
        RegistrySnapshot snapshot = _snapshot;
        IToolSource source = snapshot.FrozenSource ?? (IToolSource)_concurrentSource;
        return source.GetTool(name);
    }

    /// <inheritdoc />
    public Result Register(ITool tool)
    {
        // #1760: a tool's own budget is validated at the door, not at dispatch.
        // A zero budget would cancel the call before it started, and a negative
        // one is a typo that would otherwise read as "no deadline" — the
        // failure would surface as a mysterious instant cancellation at run
        // time instead of at the registration that caused it.
        if (!ToolTimeouts.IsValid(tool.TimeoutMs))
        {
            return Result.Failure(
                $"Tool '{tool.Name}' declares a non-positive TimeoutMs ({tool.TimeoutMs}). "
                + "A budget must be a positive number of milliseconds, or null for 'no opinion'.");
        }

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

    /// <inheritdoc />
    public IReadOnlyList<IArgSafetyPolicy> SafetyPolicies => _snapshot.SafetyPolicies;

    /// <summary>
    ///     Assembles the guard set from what is registered right now (#557).
    ///     Rebuilt on each registration change — registration is a startup/reload
    ///     event, never a hot-path call, and a stale guard set is a bypass.
    /// </summary>
    private IReadOnlyList<IArgSafetyPolicy> BuildSafetyPolicies()
    {
        var declarations = new List<ToolSafetyDeclaration>(_tools.Count);
        foreach (var tool in _tools.Values)
        {
            declarations.Add(new ToolSafetyDeclaration(tool.Name.Value, tool.SafetyProfile));
        }

        return ToolSafetyPolicies.Build(declarations);
    }

    /// <summary>
    ///     Freeze the current tool set for fast lock-free lookups.
    ///     Call after all tools are registered at startup.
    /// </summary>
    public void Freeze()
    {
        // Atomic publish: readers observe either the prior snapshot or the new
        // one, never a half-built dictionary. The volatile write has release
        // semantics, so the frozen view is fully visible before the reference
        // goes live. Guards are carried forward from the last registration
        // change, which rebuilds them from the live tool set (#557).
        _snapshot = new RegistrySnapshot(
            FrozenToolView.Build(_tools.ToFrozenDictionary(), ToDescriptor),
            _snapshot.SafetyPolicies);
    }

    private void InvalidateFrozenSnapshot()
    {
        // One volatile write publishes the invalidated source and the freshly
        // derived guards together (#2570). A reader sees the previous pair or
        // this one, so it cannot pair a newly registered tool with an older
        // guard set. Dropping the source drops its memoized per-ruleset arrays.
        _snapshot = new RegistrySnapshot(null, BuildSafetyPolicies());
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
