using Harbor.Abstractions.Agents;
using Harbor.Abstractions.Models.Identifiers;
using Harbor.Abstractions.Permissions;

namespace Harbor.Tools.Builtin.Tests;

/// <summary>
///     The sub-agent definition the task-tool suites call.
/// </summary>
/// <remarks>
///     <para>
///         The task tool resolves an agent by name, so the suites need a real
///         sub-agent definition. Two of them built the same one.
///     </para>
/// </remarks>
internal static class AgentFixtures
{
    internal static AgentDefinition SubAgent(string name) => new(
        AgentName.Create(name),
        name,
        name,
        "test-model",
        "test",
        PermissionRuleset.Empty,
        20,
        IsSubAgent: true);
}
