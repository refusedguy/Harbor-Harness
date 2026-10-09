// Real-world fixture: an async tool returning a long payload plus a failing tool.
//
// What it exercises: RwAsyncTool awaits real async work and returns an ~8KB payload,
// proving the host awaits ExecuteAsync instead of blocking on it; RwFailingTool returns
// ToolResult.Error (IsError) without throwing, proving the failing path surfaces as an
// error result for the loop rather than as an exception for the host to catch.

using System.Text.Json;
using CSharpFunctionalExtensions;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Models.Identifiers;
using Harbor.Abstractions.Permissions;
using Harbor.Abstractions.Plugins;
using Harbor.Abstractions.Tools;
using Microsoft.Extensions.Logging;

public sealed class RwAsyncPlugin : IToolPlugin
{
    public string Name => "rw-async";

    public Version Version => new(1, 0, 0);

    public Version RequiredHarborVersion => new(0, 4, 0);

    public string Description => "Real-world fixture: async long-payload tool plus failing tool.";

    public void Initialize(PluginContext context)
    {
    }

    public void RegisterTools(IToolRegistryBuilder builder)
    {
        builder.AddTool<RwAsyncTool>();
        builder.AddTool<RwFailingTool>();
    }

    public Task ShutdownAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
}

public sealed class RwAsyncTool : ITool
{
    private static readonly JsonDocument Schema = JsonDocument.Parse("{\"type\":\"object\"}");

    public ToolName Name => ToolName.Create("rw_async");

    public string DisplayName => "RW Async";

    public string Description => "Awaits async work and returns a long payload.";

    public JsonDocument ParameterSchema => Schema;

    public ExecutionMode ExecutionMode => ExecutionMode.Parallel;

    public ToolSafetyProfile SafetyProfile => ToolSafetyProfile.Opaque;

    public string? PromptSnippet => null;

    public IReadOnlyList<string> PromptGuidelines => Array.Empty<string>();

    public async Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext context, CancellationToken cancellationToken = default)
    {
        await Task.Delay(10, cancellationToken).ConfigureAwait(false);
        return ToolResult.Success(new string('x', 8000));
    }
}

public sealed class RwFailingTool : ITool
{
    private static readonly JsonDocument Schema = JsonDocument.Parse("{\"type\":\"object\"}");

    public ToolName Name => ToolName.Create("rw_failing");

    public string DisplayName => "RW Failing";

    public string Description => "Fails as an error result, never as an exception.";

    public JsonDocument ParameterSchema => Schema;

    public ExecutionMode ExecutionMode => ExecutionMode.Parallel;

    public ToolSafetyProfile SafetyProfile => ToolSafetyProfile.Opaque;

    public string? PromptSnippet => null;

    public IReadOnlyList<string> PromptGuidelines => Array.Empty<string>();

    public Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext context, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(ToolResult.Error("rw-boom: the failing path is a result, not an exception"));
    }
}
