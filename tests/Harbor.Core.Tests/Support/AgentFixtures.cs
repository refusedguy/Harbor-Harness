using Harbor.Abstractions.Agents;
using Harbor.Abstractions.Models.Identifiers;
using Harbor.Abstractions.Permissions;

namespace Harbor.Core.Tests;

/// <summary>
///     The permission suites' agent definition.
/// </summary>
/// <remarks>
///     <para>
///         The three permission suites each built the same agent with the same
///         default ruleset; only the rules differ, and the rules are the argument.
///     </para>
/// </remarks>
internal static class AgentFixtures
{
    internal static AgentDefinition AgentWithRuleset(params PermissionRule[] rules) => new(
        AgentName.Create("code"),
        "Code",
        "Default coding agent.",
        "test-model",
        "test",
        new PermissionRuleset(rules));
}
