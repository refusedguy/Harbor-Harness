using Harbor.Core.Agents;
using Harbor.Tools.Builtin;
using Harbor.Tools.Mcp;
using Harbor.Abstractions.Agents;
using Harbor.Abstractions.Tools;
using Harbor.Ui.Framework.Panels;
using Microsoft.Extensions.Logging;

namespace Harbor.Hosting;

internal static class ToolsCatalog
{
    internal static AgentRegistry CreateAgentRegistry(HarborCompositionContext ctx)
    {
        var registry = new AgentRegistry();
        var ab = new AgentRegistryBuilder(registry);
        string[] parts = ctx.Harbor.EffectiveModel.Split('/', 2);
        string providerId = parts[0];
        string modelId = parts.Length > 1 ? parts[1] : ctx.Harbor.Model;
        ab.AddAgent(AgentDefinition.CodeDefault(modelId, providerId));
        ab.AddAgent(AgentDefinition.PlanDefault(modelId, providerId));
        ab.AddAgent(AgentDefinition.ExploreDefault(modelId, providerId));
        return registry;
    }

    internal static McpRegistry CreateMcpRegistry(HarborCompositionContext ctx)
    {
        var mcpRegistry = new McpRegistry(ctx.LoggerFactory.CreateLogger<McpRegistry>());

        // Load MCP servers from the standard mcp.json files in overlay order
        // (later wins): an explicit HARBOR_MCP_CONFIG, then ~/.harbor/mcp.json,
        // then <project>/.harbor/mcp.json.
        string projectRoot = Directory.GetCurrentDirectory();
        string homeDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string harborHome = Path.Combine(homeDir, ".harbor");
        var mcpLoader = new McpServersConfigLoader(projectRoot, homeDir, harborHome);

        var mcpConfigPaths = new List<string>();
        string? explicitMcp = Environment.GetEnvironmentVariable("HARBOR_MCP_CONFIG");
        if (!string.IsNullOrEmpty(explicitMcp))
            mcpConfigPaths.Add(explicitMcp);
        mcpConfigPaths.Add(Path.Combine(harborHome, "mcp.json"));
        mcpConfigPaths.Add(Path.Combine(projectRoot, ".harbor", "mcp.json"));

        foreach (var entry in mcpLoader.Load(mcpConfigPaths.ToArray()))
            mcpRegistry.Register(entry.Name, entry.StartInfo);
        return mcpRegistry;
    }

    internal static ToolRegistry CreateToolRegistry(
        HarborCompositionContext ctx, IMcpRegistry mcpRegistry, IAgentRegistry agentRegistry)
    {
        var registry = new ToolRegistry();
        var tb = new ToolRegistryBuilder(registry, ctx.LoggerFactory);
        tb.AddTool(new ReadToolFactory());
        tb.AddTool(new WriteToolFactory());
        tb.AddTool(new EditToolFactory());
        tb.AddTool(new BashToolFactory());
        tb.AddTool(new GlobToolFactory());
        tb.AddTool(new GrepToolFactory());
        tb.AddTool(new LsToolFactory());
        tb.AddTool(new TaskToolFactory(agentRegistry));
        tb.AddTool(new WebFetchToolFactory());
        tb.AddTool(new PatchToolFactory());
        tb.AddTool(new NotebookToolFactory());
        tb.AddTool(new RipGrepToolFactory());
        tb.AddTool(new TreeToolFactory());
        tb.AddTool(new McpToolToolFactory(mcpRegistry));

        registry.Freeze();
        const int toolCount = 14;
        ctx.Logger.LogInformation("Registered {Count} tools", toolCount);
        return registry;
    }
}

file sealed class ReadToolFactory : IToolFactory
{
    public ITool CreateTool(ILoggerFactory loggerFactory) => new ReadTool(loggerFactory.CreateLogger<ReadTool>());
}

file sealed class WriteToolFactory : IToolFactory
{
    public ITool CreateTool(ILoggerFactory loggerFactory) => new WriteTool(loggerFactory.CreateLogger<WriteTool>());
}

file sealed class EditToolFactory : IToolFactory
{
    public ITool CreateTool(ILoggerFactory loggerFactory) => new EditTool(loggerFactory.CreateLogger<EditTool>());
}

file sealed class BashToolFactory : IToolFactory
{
    public ITool CreateTool(ILoggerFactory loggerFactory) => new BashTool(loggerFactory.CreateLogger<BashTool>());
}

file sealed class GlobToolFactory : IToolFactory
{
    public ITool CreateTool(ILoggerFactory loggerFactory) => new GlobTool(loggerFactory.CreateLogger<GlobTool>());
}

file sealed class GrepToolFactory : IToolFactory
{
    public ITool CreateTool(ILoggerFactory loggerFactory) => new GrepTool(loggerFactory.CreateLogger<GrepTool>());
}

file sealed class LsToolFactory : IToolFactory
{
    public ITool CreateTool(ILoggerFactory loggerFactory) => new LsTool(loggerFactory.CreateLogger<LsTool>());
}

file sealed class TaskToolFactory : IToolFactory
{
    private readonly IAgentRegistry _agentRegistry;

    public TaskToolFactory(IAgentRegistry agentRegistry)
    {
        _agentRegistry = agentRegistry;
    }

    public ITool CreateTool(ILoggerFactory loggerFactory) => new TaskTool(_agentRegistry, loggerFactory.CreateLogger<TaskTool>());
}

file sealed class WebFetchToolFactory : IToolFactory
{
    public ITool CreateTool(ILoggerFactory loggerFactory) => new WebFetchTool(loggerFactory.CreateLogger<WebFetchTool>());
}

file sealed class PatchToolFactory : IToolFactory
{
    public ITool CreateTool(ILoggerFactory loggerFactory) => new PatchTool(loggerFactory.CreateLogger<PatchTool>());
}

file sealed class NotebookToolFactory : IToolFactory
{
    public ITool CreateTool(ILoggerFactory loggerFactory) => new NotebookTool(loggerFactory.CreateLogger<NotebookTool>());
}

file sealed class RipGrepToolFactory : IToolFactory
{
    public ITool CreateTool(ILoggerFactory loggerFactory) => new RipGrepTool(loggerFactory.CreateLogger<RipGrepTool>());
}

file sealed class TreeToolFactory : IToolFactory
{
    public ITool CreateTool(ILoggerFactory loggerFactory) => new TreeTool(loggerFactory.CreateLogger<TreeTool>());
}

file sealed class McpToolToolFactory : IToolFactory
{
    private readonly IMcpRegistry _mcpRegistry;

    public McpToolToolFactory(IMcpRegistry mcpRegistry)
    {
        _mcpRegistry = mcpRegistry;
    }

    public ITool CreateTool(ILoggerFactory loggerFactory) => new McpToolTool(_mcpRegistry, loggerFactory.CreateLogger<McpToolTool>());
}
