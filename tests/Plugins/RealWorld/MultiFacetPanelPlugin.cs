// Real-world fixture: one plugin implementing three facets at once.
//
// What it exercises: RwMultiPlugin is an IToolPlugin, an ITuiPanelPlugin and an
// IAgentPlugin in a single class. The panel half names Harbor.Ui.Framework.Panels types,
// which resolve through the declared contract tier (Harbor.Ui.Framework.State by
// deployment-directory name) rather than through whatever the host happened to have loaded
// first. The test asserts all three registrations land on the host: the tool executes,
// the panel provider is stored, and the agent definition is stored.

using System.Text.Json;
using CSharpFunctionalExtensions;
using Harbor.Abstractions.Agents;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Models.Identifiers;
using Harbor.Abstractions.Permissions;
using Harbor.Abstractions.Plugins;
using Harbor.Abstractions.Tools;
using Harbor.Ui.Framework.Panels;
using Harbor.Ui.Framework.State;
using Microsoft.Extensions.Logging;

public sealed class RwMultiPlugin : IToolPlugin, ITuiPanelPlugin, IAgentPlugin
{
    public string Name => "rw-multi";

    public Version Version => new(1, 0, 0);

    public Version RequiredHarborVersion => new(0, 4, 0);

    public string Description => "Real-world fixture: tool plus panel plus agent in one plugin.";

    public void Initialize(PluginContext context)
    {
    }

    public void RegisterTools(IToolRegistryBuilder builder) =>
        builder.AddTool<RwPanelTool>();

    public void RegisterPanels(IPanelRegistry registry) =>
        registry.Register(new RwPanelProvider());

    public void RegisterAgents(IAgentRegistryBuilder builder) =>
        builder.AddAgent(new AgentDefinition(
            AgentName.Create("rw-agent"),
            "RW Agent",
            "Real-world fixture agent.",
            "test-model",
            "test-provider",
            PermissionRuleset.Default));

    public Task ShutdownAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
}

public sealed class RwPanelTool : ITool
{
    private static readonly JsonDocument Schema = JsonDocument.Parse("{\"type\":\"object\"}");

    public ToolName Name => ToolName.Create("rw_panel_tool");

    public string DisplayName => "RW Panel Tool";

    public string Description => "Tool half of the multi-facet fixture.";

    public JsonDocument ParameterSchema => Schema;

    public ExecutionMode ExecutionMode => ExecutionMode.Parallel;

    public ToolSafetyProfile SafetyProfile => ToolSafetyProfile.Opaque;

    public string? PromptSnippet => null;

    public IReadOnlyList<string> PromptGuidelines => Array.Empty<string>();

    public Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext context, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(ToolResult.Success("panel-tool-ok"));
    }
}

public sealed class RwPanelProvider : IPanelProvider
{
    public string Id => "rw-panel";

    public string Title => "RW Panel";

    public TuiPanelPlacement DefaultPlacement => TuiPanelPlacement.Bottom;

    public int DefaultSize => 5;

    public object? Build(PanelContext ctx)
    {
        // Rows of text, never a widget object: the only renderer with a panel path
        // flattens string collections and paints any other type via ToString().
        return new List<string> { "RW panel", $"width={ctx.Width}" };
    }

    public bool OnKey(UiKey key, PanelContext ctx) => false;
}
