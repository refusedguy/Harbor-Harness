using System.Collections.Frozen;
using Microsoft.Extensions.Logging;
using NonBlocking;
namespace Harbor.Registries.Tools;

public sealed class CompositeToolRegistry : IToolRegistry
{
    private readonly List<IToolSource> _sources = new();
    private volatile FrozenDictionary<ToolName, ITool>? _frozenTools;

    public void AddSource(IToolSource source)
    {
        _sources.Add(source);
        InvalidateFrozenSnapshot();
    }

    public IReadOnlyList<ToolDescriptor> GetAllTools()
    {
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
        var frozen = _frozenTools;
        if (frozen is not null)
        {
            if (sessionPermission is null)
            {
                return ResolveAllFromFrozen(frozen);
            }

            return ResolveFilteredFromFrozen(frozen, sessionPermission);
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
        var frozen = _frozenTools;
        if (frozen is not null && frozen.TryGetValue(name, out var tool))
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

        _frozenTools = dict.ToFrozenDictionary();
    }

    private void InvalidateFrozenSnapshot()
    {
        Interlocked.Exchange(ref _frozenTools, null);
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

    private static ToolDescriptor ToDescriptor(ITool t) => new(
        t.Name,
        t.DisplayName,
        t.Description,
        t.ParameterSchema,
        t.ExecutionMode,
        t.PromptSnippet,
        t.PromptGuidelines);
}
