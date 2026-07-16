using CSharpFunctionalExtensions;
using Harbor.Abstractions.Agents;
using Harbor.Abstractions.Models.Identifiers;
using NonBlocking;

namespace Harbor.Abstractions.Agents;

/// <summary>
/// Thread-safe agent registry.
/// Implements Registry pattern (GOF).
/// Backed by <see cref="NonBlocking.ConcurrentDictionary{TKey, TValue}"/> for lock-free scaling.
/// </summary>
public sealed class AgentRegistry : IAgentRegistry
{
    private readonly NonBlocking.ConcurrentDictionary<AgentName, AgentDefinition> _agents = new();

    /// <inheritdoc/>
    public IReadOnlyList<AgentDefinition> GetAllAgents()
    {
        var count = _agents.Count;
        if (count == 0)
        {
            return Array.Empty<AgentDefinition>();
        }

        var result = new AgentDefinition[count];
        var i = 0;
        foreach (var agent in _agents.Values)
        {
            result[i++] = agent;
        }
        return result;
    }

    /// <inheritdoc/>
    public Result<AgentDefinition> GetAgent(AgentName name)
    {
        if (_agents.TryGetValue(name, out var agent))
            return Result.Success(agent);

        return Result.Failure<AgentDefinition>($"Agent '{name}' is not registered.");
    }

    /// <inheritdoc/>
    public Result Register(AgentDefinition agent)
    {
        if (!_agents.TryAdd(agent.Name, agent))
            return Result.Failure($"Agent '{agent.Name}' is already registered.");

        return Result.Success();
    }

    /// <inheritdoc/>
    public Result Unregister(AgentName name)
    {
        if (_agents.TryRemove(name, out _))
            return Result.Success();

        return Result.Failure($"Agent '{name}' is not registered.");
    }
}

/// <summary>
/// Builder implementation for <see cref="IAgentRegistryBuilder"/>. Wraps an
/// <see cref="IAgentRegistry"/> and converts failures to <see cref="InvalidOperationException"/>.
/// </summary>
public sealed class AgentRegistryBuilder : IAgentRegistryBuilder
{
    private readonly IAgentRegistry _registry;

    /// <summary>
    /// Construct a builder backed by the supplied registry.
    /// </summary>
    /// <param name="registry">The registry to wrap.</param>
    public AgentRegistryBuilder(IAgentRegistry registry)
    {
        _registry = registry;
    }

    /// <inheritdoc/>
    public void AddAgent(AgentDefinition agent)
    {
        var result = _registry.Register(agent);
        if (result.IsFailure)
            throw new InvalidOperationException(result.Error);
    }
}
