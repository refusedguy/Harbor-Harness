using System.Collections.Frozen;
using System.Collections.Immutable;
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

    /// <summary>Derives the guard set from the resolved tool set (#557, #2572).</summary>
    private IReadOnlyList<IArgSafetyPolicy> BuildSafetyPolicies()
    {
        var declarations = new List<ToolSafetyDeclaration>();
        foreach (var tool in EnumerateResolvedTools(_sources))
        {
            declarations.Add(new ToolSafetyDeclaration(tool.Name.Value, tool.SafetyProfile));
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
        foreach (var tool in EnumerateResolvedTools(_sources))
        {
            dict[tool.Name] = tool;
        }

        // Guards are carried forward from the last AddSource, which rebuilds
        // them from what the sources expose (#557).
        _snapshot = new RegistrySnapshot(
            FrozenToolView.Build(dict.ToFrozenDictionary(), ToDescriptor),
            _snapshot.SafetyPolicies);
    }

    /// <summary>
    ///     The one resolution pass over the sources (#2572). Sources are walked in
    ///     registration order; a name is resolved by the first source that yields a
    ///     tool, and the result is an immutable, deduplicated array. Safety guards,
    ///     the frozen snapshot and the live read path all consume this list, so a
    ///     duplicate name can no longer resolve to different tools depending on
    ///     which path reads it. Unresolvable descriptors stay skipped, as before.
    /// </summary>
    private static IReadOnlyList<ITool> EnumerateResolvedTools(IReadOnlyList<IToolSource> sources)
    {
        var seen = new HashSet<ToolName>();
        var builder = ImmutableArray.CreateBuilder<ITool>();
        foreach (var source in sources)
        {
            foreach (var descriptor in source.GetAllTools())
            {
                var result = source.GetTool(descriptor.Name);
                if (result.IsSuccess)
                {
                    if (!seen.Add(result.Value.Name))
                    {
                        continue;
                    }

                    builder.Add(result.Value);
                }
            }
        }

        return builder.Count == 0 ? ImmutableArray<ITool>.Empty : builder.ToImmutable();
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
    ///     source itself). Every read goes through the same resolution pass as
    ///     Freeze and the safety-guard derivation (#2572).
    /// </summary>
    private sealed class CompositeLiveSource : IToolSource
    {
        private readonly IReadOnlyList<IToolSource> _sources;

        public CompositeLiveSource(IReadOnlyList<IToolSource> sources)
        {
            _sources = sources;
        }

        public IReadOnlyList<ToolDescriptor> GetAllTools() => Describe(EnumerateResolvedTools(_sources));

        public IReadOnlyList<ToolDescriptor> ResolveTools(string agentName, PermissionRuleset? sessionPermission = null)
        {
            var tools = EnumerateResolvedTools(_sources);
            if (sessionPermission is null)
            {
                return Describe(tools);
            }

            var allowed = new List<ToolDescriptor>(tools.Count);
            foreach (var tool in tools)
            {
                if (sessionPermission.Evaluate(tool.Name.Value, "*") == PermissionAction.Allow)
                {
                    allowed.Add(ToDescriptor(tool));
                }
            }

            return allowed.Count == 0 ? Array.Empty<ToolDescriptor>() : allowed.ToArray();
        }

        public Result<ITool> GetTool(ToolName name)
        {
            foreach (var tool in EnumerateResolvedTools(_sources))
            {
                if (tool.Name == name)
                {
                    return Result.Success(tool);
                }
            }

            return Result.Failure<ITool>($"Tool '{name}' is not registered.");
        }

        private static IReadOnlyList<ToolDescriptor> Describe(IReadOnlyList<ITool> tools)
        {
            int count = tools.Count;
            if (count == 0)
            {
                return Array.Empty<ToolDescriptor>();
            }

            var descriptors = new ToolDescriptor[count];
            for (int i = 0; i < count; i++)
            {
                descriptors[i] = ToDescriptor(tools[i]);
            }

            return descriptors;
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
