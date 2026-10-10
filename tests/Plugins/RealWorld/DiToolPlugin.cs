// Real-world fixture: a tool with a DI dependency resolved from the host container.
//
// What it exercises: RwDiTool takes its only dependency (ILoggerFactory) through its
// constructor via the builder.AddTool(lf => ...) overload, and RwDiPlugin captures
// PluginContext.LoggerFactory during Initialize. The test asserts both references are
// the host singleton by identity, closing the "ToolContext carries no container" trap
// (#470): production call sites pass no service provider, so a tool that reads its
// dependencies from the execution context gets nothing — the constructor is the wiring.

using System.Text.Json;
using CSharpFunctionalExtensions;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Models.Identifiers;
using Harbor.Abstractions.Permissions;
using Harbor.Abstractions.Plugins;
using Harbor.Abstractions.Tools;
using Microsoft.Extensions.Logging;

public sealed class RwDiPlugin : IToolPlugin
{
    public static ILoggerFactory? CapturedLoggerFactory;

    public string Name => "rw-di";

    public Version Version => new(1, 0, 0);

    public Version RequiredHarborVersion => new(0, 4, 0);

    public string Description => "Real-world fixture: ctor-injected ILoggerFactory tool.";

    public void Initialize(PluginContext context)
    {
        CapturedLoggerFactory = context.LoggerFactory;
    }

    public void RegisterTools(IToolRegistryBuilder builder) =>
        builder.AddTool(lf => new RwDiTool(lf));

    public Task ShutdownAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
}

public sealed class RwDiTool : ITool
{
    private static readonly JsonDocument Schema = JsonDocument.Parse("{\"type\":\"object\"}");

    private readonly ILoggerFactory _loggerFactory;

    public RwDiTool(ILoggerFactory loggerFactory)
    {
        _loggerFactory = loggerFactory;
    }

    public ToolName Name => ToolName.Create("rw_di");

    public string DisplayName => "RW DI";

    public string Description => "Proves its ILoggerFactory came from the host container.";

    public JsonDocument ParameterSchema => Schema;

    public ExecutionMode ExecutionMode => ExecutionMode.Parallel;

    public ToolSafetyProfile SafetyProfile => ToolSafetyProfile.Opaque;

    public string? PromptSnippet => null;

    public IReadOnlyList<string> PromptGuidelines => Array.Empty<string>();

    public Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext context, CancellationToken cancellationToken = default)
    {
        // Never touches the execution context for services: there is no container on
        // ToolContext by design (#470), so the factory must arrive via the constructor.
        string factory = _loggerFactory.GetType().Name;
        return Task.FromResult(ToolResult.Success($"di-ok factory={factory}"));
    }
}
