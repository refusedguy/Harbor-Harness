using Harbor.Application.Agents;
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

        string defaultModel = ctx.Options.ModelSource == HarborAgentModelSource.CommonConfig
            ? ResolveDefaultModelFromCommon(ctx.Common)
            : ctx.Harbor.EffectiveModel;
        string[] parts = defaultModel.Split('/', 2);
        string providerId = parts[0];
        string modelId = parts.Length > 1 ? parts[1] : defaultModel;
        ab.AddAgent(AgentDefinition.CodeDefault(modelId, providerId));
        ab.AddAgent(AgentDefinition.PlanDefault(modelId, providerId));
        ab.AddAgent(AgentDefinition.ExploreDefault(modelId, providerId));
        return registry;
    }

    /// <summary>HARBOR_MODEL env, else CommonConfig DefaultProvider/DefaultModel (desktop).</summary>
    internal static string ResolveDefaultModelFromCommon(Harbor.Desktop.Abstractions.Configuration.CommonConfig commonConfig)
    {
        string? env = Environment.GetEnvironmentVariable("HARBOR_MODEL");
        if (!string.IsNullOrWhiteSpace(env)) return env;

        string model = commonConfig.DefaultModel;
        string provider = commonConfig.DefaultProvider;
        string prefix = provider + "/";
        return model.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? model
            : prefix + model;
    }

    internal static IMcpRegistry CreateMcpRegistry(HarborCompositionContext ctx)
    {
        if (!ctx.Options.IncludeMcpTools)
        {
            // Desktop subset: empty registry so view-models can resolve IMcpRegistry.
            return new InMemoryMcpRegistry(ctx.LoggerFactory.CreateLogger<InMemoryMcpRegistry>());
        }

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
        HarborCompositionContext ctx, IMcpRegistry mcpRegistry, IAgentRegistry agentRegistry,
        Harbor.Abstractions.Agents.ISubAgentRunner subAgentRunner,
        Harbor.Abstractions.Lsp.ILspService? lspService = null)
    {
        var registry = new ToolRegistry();
        var tb = new ToolRegistryBuilder(registry, ctx.LoggerFactory);
        bool full = ctx.Options.ToolSet == HarborToolSetKind.Full14;

        // P.2: logger-aware lambdas replaced 14 IToolFactory ceremony classes.
        tb.AddTool(lf => new ReadTool(lf.CreateLogger<ReadTool>()));
        tb.AddTool(lf => new WriteTool(lf.CreateLogger<WriteTool>()));
        tb.AddTool(lf => new EditTool(lf.CreateLogger<EditTool>()));
        tb.AddTool(lf => new BashTool(lf.CreateLogger<BashTool>()));
        tb.AddTool(lf => new GlobTool(lf.CreateLogger<GlobTool>()));
        tb.AddTool(lf => new GrepTool(lf.CreateLogger<GrepTool>()));
        tb.AddTool(lf => new LsTool(lf.CreateLogger<LsTool>()));
        if (full)
        {
            tb.AddTool(lf => new TaskTool(agentRegistry, lf.CreateLogger<TaskTool>(), subAgentRunner));
            tb.AddTool(lf => new WebFetchTool(lf.CreateLogger<WebFetchTool>()));
        }
        tb.AddTool(lf => new PatchTool(lf.CreateLogger<PatchTool>()));
        tb.AddTool(lf => new NotebookTool(lf.CreateLogger<NotebookTool>()));
        if (full)
        {
            tb.AddTool(lf => new RipGrepTool(lf.CreateLogger<RipGrepTool>()));
        }
        tb.AddTool(lf => new TreeTool(lf.CreateLogger<TreeTool>()));
        if (lspService is not null)
        {
            tb.AddTool(new LspTool(lspService, ctx.LoggerFactory.CreateLogger<LspTool>()));
        }
        if (full)
        {
            tb.AddTool(lf => new McpToolTool(mcpRegistry, lf.CreateLogger<McpToolTool>()));
        }

        registry.Freeze();
        ctx.Logger.LogInformation("Registered {Count} tools", registry.GetAllTools().Count);
        return registry;
    }
}
