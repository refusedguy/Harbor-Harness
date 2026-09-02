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
    public string DisplayName => $"{_server}:{_toolName}";
    public string Description => $"MCP tool {_toolName} on server {_server}";
    public ExecutionMode ExecutionMode => ExecutionMode.Sequential;
    public string? PromptSnippet => $"mcp {_server}/{_toolName}: {Description}";
    public IReadOnlyList<string> PromptGuidelines { get; } = Array.Empty<string>();
    public JsonDocument ParameterSchema { get; } = JsonDocument.Parse("""
        {
          "type": "object",
          "properties": {
            "arguments": { "type": "object", "description": "Tool arguments" }
          }
        }
        """);

    public Result ValidateArguments(JsonElement args)
    {
        if (args.TryGetProperty("arguments", out var a) && a.ValueKind != JsonValueKind.Object)
            return Result.Failure("'arguments' must be a JSON object.");
        return Result.Success();
    }

    public async Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext context, CancellationToken cancellationToken = default)
    {
        JsonElement methodArgs = args.TryGetProperty("arguments", out var a) && a.ValueKind == JsonValueKind.Object
            ? a
            : default;

        using var argsDoc = JsonDocument.Parse($"{{\"name\":\"{_toolName}\",\"arguments\":{methodArgs.GetRawText()}}}");
        // ROP-A Z1 п.17: boundary Match.
        return await _registry.InvokeAsync(_server, "tools/call", argsDoc.RootElement.Clone(), cancellationToken)
            .Match(
                static value => ToolResult.Success(value),
                static error => ToolResult.Error($"MCP tool call failed: {error}"))
            .ConfigureAwait(false);
    }
}
