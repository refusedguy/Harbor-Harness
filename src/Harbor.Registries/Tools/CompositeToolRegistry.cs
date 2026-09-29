using System.Collections.Frozen;
using Microsoft.Extensions.Logging;
using NonBlocking;
namespace Harbor.Registries.Tools;

public sealed class CompositeToolRegistry : IToolRegistry
{
    private readonly List<IToolSource> _sources = new();
    // #183: single volatile publish-once snapshot — see FrozenToolView.
    private volatile FrozenToolView? _frozenView;

    // #557: the guard set implied by the tools the sources expose, rebuilt on every
    // AddSource so a path-taking tool is guarded by being registered, never by
    // being remembered in a list.
    private volatile IReadOnlyList<IArgSafetyPolicy> _safetyPolicies = ToolSafetyPolicies.Build([]);

    /// <inheritdoc />
    public IReadOnlyList<IArgSafetyPolicy> SafetyPolicies => _safetyPolicies;

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
        // Frozen snapshot: the cached array is returned as-is (zero alloc).
        var view = _frozenView;
        if (view is not null)
        {
            return view.GetAll();
        }

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
        // Frozen snapshot: cached array (unfiltered) or memoized per-ruleset
        // array (filtered) — zero alloc on repeat calls. Treat as read-only.
        var view = _frozenView;
        if (view is not null)
        {
            return view.Resolve(sessionPermission);
        }

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
        var view = _frozenView;
        if (view is not null && view.TryGetTool(name, out var tool) && tool is not null)
        {
            return Result.Success(tool);
        }

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

        _frozenView = FrozenToolView.Build(dict.ToFrozenDictionary(), ToDescriptor);
    }

    private void InvalidateFrozenSnapshot()
    {
        Interlocked.Exchange(ref _frozenView, null);
        // #557: same contract as ToolRegistry — the guard set is derived from what
        // the sources expose, so a path-taking tool in a source is guarded without
        // any list to update.
        _safetyPolicies = BuildSafetyPolicies();
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
