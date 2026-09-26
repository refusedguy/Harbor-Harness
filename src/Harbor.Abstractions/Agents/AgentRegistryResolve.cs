using Harbor.Abstractions.Models.Identifiers;

namespace Harbor.Abstractions.Agents;

/// <summary>
///     Shared resolve preamble for the <c>TryCreate → GetAgent</c> chain
///     (ROP boundary batch #101). Every <see cref="IAgentRegistry" />
///     consumer resolves an agent name string the same way so parse and lookup
///     failures surface as one <c>Result</c>, never a throw.
/// </summary>
public static class AgentRegistryResolve
{
    /// <summary>
    ///     Parse <paramref name="agentName" /> and resolve the registered
    ///     definition in a single Bind railway.
    /// </summary>
    /// <param name="agents">The agent registry to look up.</param>
    /// <param name="agentName">The raw agent name string (may be null/blank).</param>
    /// <returns>Success with the definition, or failure with the parse/lookup reason.</returns>
    public static Result<AgentDefinition> ResolveAgent(this IAgentRegistry agents, string? agentName) =>
        AgentName.TryCreate(agentName).Bind(agents.GetAgent);
}
