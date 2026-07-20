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
    ///     Build the <see cref="ToolRegistry"/> eagerly with every builtin
    ///     tool registered. The returned registry is registered as a
    ///     singleton by the caller.
    /// </summary>
    /// <param name="loggerFactory">Bootstrap logger factory (used to construct per-tool loggers).</param>
    /// <returns>The constructed + frozen <see cref="ToolRegistry"/>.</returns>
    public static ToolRegistry Build(ILoggerFactory loggerFactory)
    {
        var toolRegistry = new ToolRegistry();
        var tb = new ToolRegistryBuilder(toolRegistry);
        tb.AddTool(() => new ReadTool(loggerFactory.CreateLogger<ReadTool>()));
        tb.AddTool(() => new WriteTool(loggerFactory.CreateLogger<WriteTool>()));
        tb.AddTool(() => new EditTool(loggerFactory.CreateLogger<EditTool>()));
        tb.AddTool(() => new BashTool(loggerFactory.CreateLogger<BashTool>()));
        tb.AddTool(() => new GlobTool(loggerFactory.CreateLogger<GlobTool>()));
        tb.AddTool(() => new GrepTool(loggerFactory.CreateLogger<GrepTool>()));
        tb.AddTool(() => new LsTool(loggerFactory.CreateLogger<LsTool>()));
        tb.AddTool(() => new PatchTool(loggerFactory.CreateLogger<PatchTool>()));
        tb.AddTool(() => new NotebookTool(loggerFactory.CreateLogger<NotebookTool>()));
        tb.AddTool(() => new TreeTool(loggerFactory.CreateLogger<TreeTool>()));
        toolRegistry.Freeze();
        return toolRegistry;
    }
}
