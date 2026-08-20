using Harbor.Abstractions.Tools;
using Harbor.Tools.Builtin;
using Microsoft.Extensions.Logging;
namespace Harbor.App.Avalonia.Hosting;
/// <summary>
///     Builtin-tool registration — a subset of the 10 builtin tools
///     (no MCP, no WebFetch to avoid HTTP policy decisions; the user can
///     add them via Settings later).
/// </summary>
internal static class ToolRegistration
{
    /// <summary>
    ///     Build the <see cref="ToolRegistry" /> eagerly with every builtin
    ///     tool registered. The returned registry is registered as a
    ///     singleton by the caller.
    /// </summary>
    /// <param name="loggerFactory">Bootstrap logger factory (used to construct per-tool loggers).</param>
    /// <returns>The constructed + frozen <see cref="ToolRegistry" />.</returns>
    public static ToolRegistry Build(ILoggerFactory loggerFactory)
    {
        var toolRegistry = new ToolRegistry();
        var tb = new ToolRegistryBuilder(toolRegistry, loggerFactory);
        tb.AddTool(new ReadToolFactory());
        tb.AddTool(new WriteToolFactory());
        tb.AddTool(new EditToolFactory());
        tb.AddTool(new BashToolFactory());
        tb.AddTool(new GlobToolFactory());
        tb.AddTool(new GrepToolFactory());
        tb.AddTool(new LsToolFactory());
        tb.AddTool(new PatchToolFactory());
        tb.AddTool(new NotebookToolFactory());
        tb.AddTool(new TreeToolFactory());
        toolRegistry.Freeze();
        return toolRegistry;
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

file sealed class PatchToolFactory : IToolFactory
{
    public ITool CreateTool(ILoggerFactory loggerFactory) => new PatchTool(loggerFactory.CreateLogger<PatchTool>());
}

file sealed class NotebookToolFactory : IToolFactory
{
    public ITool CreateTool(ILoggerFactory loggerFactory) => new NotebookTool(loggerFactory.CreateLogger<NotebookTool>());
}

file sealed class TreeToolFactory : IToolFactory
{
    public ITool CreateTool(ILoggerFactory loggerFactory) => new TreeTool(loggerFactory.CreateLogger<TreeTool>());
}
