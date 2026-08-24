using System.Text.Json;
using CSharpFunctionalExtensions;
using Harbor.Abstractions.Agents;
using Harbor.Abstractions.Events;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Models.Identifiers;
using Harbor.Abstractions.Permissions;
using Harbor.Abstractions.Sessions;
using Harbor.Abstractions.Tools;

namespace Harbor.TestKit;

/// <summary>Registry containing exactly the given agents (P.6 canonical fake).</summary>
public sealed class FakeAgentRegistry(params AgentDefinition[] agents) : IAgentRegistry
{
    private readonly Dictionary<string, AgentDefinition> _agents =
        agents.ToDictionary(a => a.Name.Value, StringComparer.Ordinal);

    public IReadOnlyList<AgentDefinition> GetAllAgents() => [.. _agents.Values];

    public Result<AgentDefinition> GetAgent(AgentName name) =>
        _agents.TryGetValue(name.Value, out AgentDefinition? definition)
            ? Result.Success(definition)
            : Result.Failure<AgentDefinition>($"Agent '{name.Value}' is not registered.");

    public Result Register(AgentDefinition agent) =>
        _agents.TryAdd(agent.Name.Value, agent)
            ? Result.Success()
            : Result.Failure($"Agent '{agent.Name.Value}' is already registered.");

    public Result Unregister(AgentName name) =>
        _agents.Remove(name.Value)
            ? Result.Success()
            : Result.Failure($"Agent '{name.Value}' is not registered.");
}

/// <summary>In-memory tool registry over the given tools.</summary>
public sealed class FakeToolRegistry(params ITool[] tools) : IToolRegistry
{
    private readonly Dictionary<string, ITool> _tools =
        tools.ToDictionary(t => t.Name.Value, StringComparer.Ordinal);

    public IReadOnlyList<ToolDescriptor> GetAllTools() => Snapshot();

    public IReadOnlyList<ToolDescriptor> ResolveTools(string agentName, PermissionRuleset? sessionPermission = null)
        => Snapshot();

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

/// <summary>Tool that counts executions and records raw args — a canary for permission/timeout tests.</summary>
public sealed class CountingTool : ITool
{
    private readonly object _lock = new();
    private int _executions;

    public int Executions => Volatile.Read(ref _executions);

    public List<string> ExecutedArgs { get; } = [];

    public ToolName Name => ToolName.Create("counter");

    public string DisplayName => "Counter";

    public string Description => "Counts executions.";

    public JsonDocument ParameterSchema { get; } = JsonDocument.Parse(
        """{"type":"object","properties":{"n":{"type":"number"}}}""");

    public ExecutionMode ExecutionMode => ExecutionMode.Parallel;

    public string? PromptSnippet => null;

    public IReadOnlyList<string> PromptGuidelines => [];

    public Result ValidateArguments(JsonElement args) => Result.Success();

    public Task<ToolResult> ExecuteAsync(
        JsonElement args,
        ToolContext context,
        CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _executions);
        lock (_lock)
        {
            ExecutedArgs.Add(args.GetRawText());
        }

        return Task.FromResult(ToolResult.Success("counted"));
    }
}
