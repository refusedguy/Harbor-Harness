using Harbor.Abstractions.Agents;
using Harbor.Abstractions.Tools;
using Harbor.Core.Tools;
using Harbor.Tools.Builtin;
using Harbor.Tools.Mcp;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Harbor.Cli.Hosting;

internal static partial class HostBuilder
{
    private static ToolRegistry CreateToolRegistry(IServiceProvider sp, IMcpRegistry mcpRegistry, IAgentRegistry agentRegistry)
    {
        var registry = new ToolRegistry();
        var tb = new ToolRegistryBuilder(registry, sp.GetRequiredService<ILoggerFactory>());
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
        _logger.LogInformation("Registered {Count} tools", toolCount);
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
