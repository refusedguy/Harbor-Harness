using Harbor.Abstractions.Models.Identifiers;
using Harbor.Abstractions.Permissions;
namespace Harbor.Abstractions.Tools;

public interface IToolSource
{
    public IReadOnlyList<ToolDescriptor> GetAllTools();
    public IReadOnlyList<ToolDescriptor> ResolveTools(string agentName, PermissionRuleset? sessionPermission = null);
    public Result<ITool> GetTool(ToolName name);
}
