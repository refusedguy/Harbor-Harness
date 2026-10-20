using Harbor.Abstractions.Tools;
using Microsoft.Extensions.Logging;

namespace Harbor.Tools.Mcp;

public sealed class McpToolAdapter : ITool
{
    private readonly IMcpRegistry _registry;
    private readonly string _server;
    private readonly string _toolName;
    private readonly ILogger<McpToolAdapter>? _logger;

    public McpToolAdapter(IMcpRegistry registry, string server, string toolName, ILogger<McpToolAdapter>? logger = null)
    {
        _registry = registry;
        _server = server;
        _toolName = toolName;
        _logger = logger;
    }

    public ToolName Name => ToolName.Create($"mcp_{_server}_{_toolName}");

    /// <inheritdoc />
    public ToolSafetyProfile SafetyProfile => ToolSafetyProfile.Opaque;

    public string DisplayName => $"{_server}:{_toolName}";
    public string Description => $"MCP tool {_toolName} on server {_server}";
    public ExecutionMode ExecutionMode => ExecutionMode.Sequential;
    public string? PromptSnippet => $"mcp {_server}/{_toolName}: {Description}";
    public IReadOnlyList<string> PromptGuidelines { get; } = Array.Empty<string>();
    /// <summary>
    ///     The tool's parameter schema. Parsed once per process (#1574) — the JSON
    ///     below is a compile-time constant, so re-parsing it for every tool
    ///     instance was pure allocation churn.
    /// </summary>
    private static readonly JsonDocument Schema = JsonDocument.Parse("""
        {
          "type": "object",
          "properties": {
            "arguments": { "type": "object", "description": "Tool arguments" }
          }
        }
        """);
    /// <inheritdoc />
    public JsonDocument ParameterSchema => Schema;

    public Result ValidateArguments(JsonElement args) =>
        JsonArgValidator.OptionalObject(args, "arguments", "'arguments' must be a JSON object.");

    public async Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext context, CancellationToken cancellationToken = default)
    {
        JsonElement methodArgs = JsonArgs.GetObject(args, "arguments");

        JsonElement argsEl = McpJsonRpc.BuildToolCallParams(_toolName, methodArgs);
        // ROP-A Z1 п.17: boundary Match.
        return await _registry.InvokeAsync(_server, "tools/call", argsEl, cancellationToken)
            .Match(
                static value => ToolResult.Success(value),
                static error => ToolResult.Error($"MCP tool call failed: {error}"))
            .ConfigureAwait(false);
    }
}
