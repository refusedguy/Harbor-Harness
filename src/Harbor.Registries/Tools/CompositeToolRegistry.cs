using System.Collections.Frozen;
using Microsoft.Extensions.Logging;
using NonBlocking;
namespace Harbor.Registries.Tools;

public sealed class CompositeToolRegistry : IToolRegistry
{
    private readonly List<IToolSource> _sources = new();
    private readonly CompositeLiveSource _liveSource;

    /// <summary>
    ///     One publication unit: the frozen view and the safety policies derived
    ///     from the sources it was built from (#2570, #557).
    /// </summary>
    private sealed record RegistrySnapshot(
        FrozenToolView? FrozenView,
        IReadOnlyList<IArgSafetyPolicy> SafetyPolicies)
    {
        public static RegistrySnapshot Empty { get; } = new(null, ToolSafetyPolicies.Build([]));
    }

    // #183: publish-once snapshot, see FrozenToolView. One volatile write pairs
    // source and guards, so no reader can observe them from different generations.
    private volatile RegistrySnapshot _snapshot = RegistrySnapshot.Empty;

    /// <inheritdoc />
    public IReadOnlyList<IArgSafetyPolicy> SafetyPolicies => _snapshot.SafetyPolicies;

    public CompositeToolRegistry()
    {
        _liveSource = new CompositeLiveSource(_sources);
    }

    /// <summary>Derives the guard set from every source's tool list (#557).</summary>
    private IReadOnlyList<IArgSafetyPolicy> BuildSafetyPolicies()
    {
        var declarations = new List<ToolSafetyDeclaration>();
        foreach (var source in _sources)
        {
            foreach (var descriptor in source.GetAllTools())
            {
                Result<ITool> resolved = source.GetTool(descriptor.Name);
                if (resolved.IsSuccess)
                {
                    declarations.Add(new ToolSafetyDeclaration(descriptor.Name.Value, resolved.Value.SafetyProfile));
                }
            }
        }

        return ToolSafetyPolicies.Build(declarations);
    }

    public void AddSource(IToolSource source)
    {
        _sources.Add(source);
        InvalidateFrozenSnapshot();
    }

    public IReadOnlyList<ToolDescriptor> GetAllTools()
    {
        // #1101 dual-path (RESOLVED, §OOP-005): single IToolSource path like
        // ToolRegistry. One volatile read selects source and guards as a pair
        // (#2570); no duplicated lookup logic here.
        RegistrySnapshot snapshot = _snapshot;
        IToolSource source = snapshot.FrozenView ?? (IToolSource)_liveSource;
        return source.GetAllTools();
    }

    public IReadOnlyList<ToolDescriptor> ResolveTools(string agentName, PermissionRuleset? sessionPermission = null)
    {
        // #1101 dual-path (RESOLVED): same single-path selection; the frozen
        // view serves cached/memoized arrays, the live source fans out. One
        // volatile read keeps source and guards from the same generation.
        RegistrySnapshot snapshot = _snapshot;
        IToolSource source = snapshot.FrozenView ?? (IToolSource)_liveSource;
        return source.ResolveTools(agentName, sessionPermission);
    }

    public Result<ITool> GetTool(ToolName name)
    {
        // #1101 dual-path (RESOLVED): frozen fast path first, live fan-out
        // inside the source selection.
        RegistrySnapshot snapshot = _snapshot;
        IToolSource source = snapshot.FrozenView ?? (IToolSource)_liveSource;
        return source.GetTool(name);
    }

    public Result Register(ITool tool) => Result.Failure("CompositeToolRegistry is read-only. Use AddSource to add tools.");

    public Result Unregister(ToolName name) => Result.Failure("CompositeToolRegistry is read-only.");

    public void Freeze()
    {
        var dict = new Dictionary<ToolName, ITool>();
        foreach (var source in _sources)
        {
            foreach (var descriptor in source.GetAllTools())
            {
                var result = source.GetTool(descriptor.Name);
                if (result.IsSuccess)
                {
                    dict[descriptor.Name] = result.Value;
                }
            }
        }

        // Guards are carried forward from the last AddSource, which rebuilds
        // them from what the sources expose (#557).
        _snapshot = new RegistrySnapshot(
            FrozenToolView.Build(dict.ToFrozenDictionary(), ToDescriptor),
            _snapshot.SafetyPolicies);
    }

    private void InvalidateFrozenSnapshot()
    {
        // One volatile write publishes the invalidated view and the freshly
        // derived guards together (#2570). A path-taking tool in a source is
        // guarded by being registered, never by a hand-maintained list (#557).
        _snapshot = new RegistrySnapshot(null, BuildSafetyPolicies());
    }

    /// <summary>
    ///     Live fan-out <see cref="IToolSource" />: the unfrozen read path.
    ///     Holds the source-list reference (no per-call allocation for the
    ///     source itself). Semantics mirror the previous inline else-branches.
    /// </summary>
    private sealed class CompositeLiveSource : IToolSource
    {
        private readonly List<IToolSource> _sources;

        public CompositeLiveSource(List<IToolSource> sources)
        {
            _sources = sources;
        }

        public IReadOnlyList<ToolDescriptor> GetAllTools()
        {
            int count = 0;
            foreach (var source in _sources)
            {
                count += source.GetAllTools().Count;
            }

            if (count == 0)
            {
                return Array.Empty<ToolDescriptor>();
            }

            var list = new List<ToolDescriptor>(count);
            foreach (var source in _sources)
            {
                foreach (var t in source.GetAllTools())
                {
                    list.Add(t);
                }
            }
            return list;
        }

        public IReadOnlyList<ToolDescriptor> ResolveTools(string agentName, PermissionRuleset? sessionPermission = null)
        {
            var list = new List<ToolDescriptor>();
            foreach (var source in _sources)
            {
                foreach (var t in source.ResolveTools(agentName, sessionPermission))
                {
                    list.Add(t);
                }
            }
            return list;
        }

        public Result<ITool> GetTool(ToolName name)
        {
            // §4.6-ok: fold «первый успех» (rop-final-mile L8) — осознанный императивный цикл,
            // LINQ-эквивалент требует Result?-нуля либо ToArray+Match (дороже/опаснее на горячем пути).
            foreach (var source in _sources)
            {
                var result = source.GetTool(name);
                if (result.IsSuccess)
                {
                    return result;
                }
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
