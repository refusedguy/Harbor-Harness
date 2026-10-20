using CSharpFunctionalExtensions;
using Harbor.Abstractions.Models.Identifiers;
using Harbor.Abstractions.Permissions;
using Harbor.Abstractions.Tools;

namespace Harbor.TestKit;

/// <summary>In-memory tool registry over the given tools.</summary>
public sealed class FakeToolRegistry(params ITool[] tools) : IToolRegistry
{
    private readonly Dictionary<string, ITool> _tools =
        tools.ToDictionary(t => t.Name.Value, StringComparer.Ordinal);

    public IReadOnlyList<ToolDescriptor> GetAllTools() => Snapshot();

    public IReadOnlyList<ToolDescriptor> ResolveTools(string agentName, PermissionRuleset? sessionPermission = null)
        => Snapshot();

    // #557: derived from the tools themselves, exactly like the production registry.
    public IReadOnlyList<IArgSafetyPolicy> SafetyPolicies => ToolSafetyPolicies.Build(
        _tools.Values.Select(t => new ToolSafetyDeclaration(t.Name.Value, t.SafetyProfile)));

    public Result<ITool> GetTool(ToolName name) =>
        _tools.TryGetValue(name.Value, out ITool? tool)
            ? Result.Success(tool)
            : Result.Failure<ITool>($"Unknown tool '{name.Value}'.");

    public Result Register(ITool tool) =>
        _tools.TryAdd(tool.Name.Value, tool)
            ? Result.Success()
            : Result.Failure($"Tool '{tool.Name.Value}' is already registered.");

    public Result Unregister(ToolName name) =>
        _tools.Remove(name.Value)
            ? Result.Success()
            : Result.Failure($"Tool '{name.Value}' is not registered.");

    private IReadOnlyList<ToolDescriptor> Snapshot()
    {
        var list = new List<ToolDescriptor>(_tools.Values.Count);
        foreach (ITool t in _tools.Values)
        {
            list.Add(new ToolDescriptor(
                t.Name, t.DisplayName, t.Description, t.ParameterSchema,
                t.ExecutionMode, t.PromptSnippet, t.PromptGuidelines));
        }

        return list;
    }
}
