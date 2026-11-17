using Harbor.Abstractions.Tools;
using Harbor.Tools.Builtin;
using Microsoft.Extensions.Logging;

namespace Harbor.Tools.Mcp;

[ToolSchema]
[ToolParam("arguments", "object", Description = "Tool arguments")]
public sealed partial class McpToolAdapter : ITool
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

    /// <summary>
    ///     The name is derived from the SERVER and the remote tool name, not from a
    ///     process instance. A crashed server is respawned by
    ///     <c>McpRegistry.ServerEntry.GetProcess</c> on the next call, so the adapter
    ///     survives a restart without being re-registered (#2227).
    /// </summary>
    public ToolName Name => BuildName(_server, _toolName);

    /// <summary>
    ///     Build the stable tool name for one server/tool pair. Discovery and the
    ///     adapter call this same helper, so the permission rule granted at
    ///     registration names exactly the tool the dispatcher resolves.
    /// </summary>
    internal static ToolName BuildName(string server, string remoteName)
        => ToolName.Create($"mcp_{Sanitize(server)}_{Sanitize(remoteName)}");

    private static string Sanitize(string value)
    {
        // ToolName accepts ^[a-z][a-z0-9_]*$. Server names and MCP tool names are
        // free-form, so an unknown character becomes '_' rather than throwing at
        // discovery time. The "mcp_" prefix keeps the first character a letter.
        char[] chars = new char[value.Length];
        for (int i = 0; i < value.Length; i++)
        {
            char c = char.ToLowerInvariant(value[i]);
            chars[i] = (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '_' ? c : '_';
        }
        return new string(chars);
    }

    /// <inheritdoc />
    public ToolSafetyProfile SafetyProfile => ToolSafetyProfile.Opaque;

    public string DisplayName => $"{_server}:{_toolName}";
    public string Description => $"MCP tool {_toolName} on server {_server}";
    public ExecutionMode ExecutionMode => ExecutionMode.Sequential;
    public string? PromptSnippet => $"mcp {_server}/{_toolName}: {Description}";
    public IReadOnlyList<string> PromptGuidelines { get; } = Array.Empty<string>();
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
