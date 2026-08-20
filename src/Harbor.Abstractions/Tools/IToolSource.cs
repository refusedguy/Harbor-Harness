using Harbor.Abstractions.Models.Identifiers;
using Harbor.Abstractions.Permissions;

namespace Harbor.Abstractions.Tools;

public interface IToolSource
{
    IReadOnlyList<ToolDescriptor> GetAllTools();
    IReadOnlyList<ToolDescriptor> ResolveTools(string agentName, PermissionRuleset? sessionPermission = null);
    Result<ITool> GetTool(ToolName name);
}
