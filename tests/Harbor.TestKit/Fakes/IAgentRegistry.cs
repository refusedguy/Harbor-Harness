using CSharpFunctionalExtensions;
using Harbor.Abstractions.Agents;
using Harbor.Abstractions.Models.Identifiers;

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
