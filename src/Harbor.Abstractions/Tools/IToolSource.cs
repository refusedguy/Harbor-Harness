using Harbor.Abstractions.Models.Identifiers;
using Harbor.Abstractions.Permissions;

namespace Harbor.Abstractions.Tools;

/// <summary>
///     Supplies the tool descriptors an agent may call and resolves them into
///     live tool instances.
/// </summary>
public interface IToolSource
{
    /// <summary>All tool descriptors the source knows about.</summary>
    IReadOnlyList<ToolDescriptor> GetAllTools();

    /// <summary>
    ///     Resolves the descriptors available to one agent, filtered by the
    ///     session's permission ruleset when one is supplied.
    /// </summary>
    /// <param name="agentName">The agent whose tool set is resolved.</param>
    /// <param name="sessionPermission">Optional session-scoped permission ruleset.</param>
    /// <returns>The descriptors the agent may call.</returns>
    IReadOnlyList<ToolDescriptor> ResolveTools(string agentName, PermissionRuleset? sessionPermission = null);

    /// <summary>
    ///     Resolves a single tool by name.
    /// </summary>
    /// <param name="name">The tool name.</param>
    /// <returns>The tool, or a failure naming the reason it is unavailable.</returns>
    Result<ITool> GetTool(ToolName name);
}
