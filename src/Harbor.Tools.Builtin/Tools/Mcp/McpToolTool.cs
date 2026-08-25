using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Result = CSharpFunctionalExtensions.Result;

namespace Harbor.Tools.Builtin;
/// <summary>
///     Bridge to Model Context Protocol (MCP) servers. The agent calls a named server's
///     method via the <see cref="IMcpRegistry" />; the registry looks up the server,
///     transports the JSON-RPC call, and returns the response payload as a string.
/// </summary>
public sealed class McpToolTool : ITool
{
    private readonly ILogger<McpToolTool> _logger;
    private readonly IMcpRegistry? _registry;

    /// <summary>
    ///     Construct an <see cref="McpToolTool" /> that resolves the registry from
    ///     <see cref="ToolContext.Services" /> on each call (preferred for DI).
    /// </summary>
    /// <param name="logger">Logger for diagnostics.</param>
    public McpToolTool(ILogger<McpToolTool> logger)
    {
        _logger = logger;
    }

    /// <summary>
    ///     Construct an <see cref="McpToolTool" /> with a fixed registry (used in tests
    ///     where the DI container is not configured).
    /// </summary>
    /// <param name="registry">The MCP registry to use.</param>
    /// <param name="logger">Logger for diagnostics.</param>
    public McpToolTool(IMcpRegistry registry, ILogger<McpToolTool> logger) : this(logger)
    {
        _registry = registry;
    }

    /// <inheritdoc />
    public ToolName Name => ToolName.Create("mcp");

    /// <inheritdoc />
    public string DisplayName => "MCP";

    /// <inheritdoc />
    public string Description =>
        "Invoke a method on a registered Model Context Protocol server. " +
        "Servers are registered via IMcpRegistry.Register(name, stdioCmd). " +
        "Common methods: tools/list, tools/call, resources/list, prompts/list.";

    /// <inheritdoc />
    public ExecutionMode ExecutionMode => ExecutionMode.Sequential;

    /// <inheritdoc />
    public string? PromptSnippet => "mcp: Invoke a method on a registered MCP server";

    /// <inheritdoc />
    public IReadOnlyList<string> PromptGuidelines { get; } =
    [
        "Use `mcp` to call tools exposed by MCP servers (filesystem, db, browser, …)",
        "Server must be registered first via IMcpRegistry.Register(name, stdioCmd)",
        "args is a JSON object — for tools/call include `name` and `arguments`",
        "Result is the server's JSON response serialized to a string"
    ];

    /// <inheritdoc />
    public JsonDocument ParameterSchema { get; } = JsonDocument.Parse("""
                                                                      {
                                                                        "type": "object",
                                                                        "properties": {
                                                                          "server":  { "type": "string", "description": "Registered MCP server name" },
                                                                          "method":  { "type": "string", "description": "JSON-RPC method (e.g. 'tools/list', 'tools/call')" },
                                                                          "args":    { "type": "object", "description": "Arguments object (e.g. {\"name\":\"read_file\",\"arguments\":{\"path\":\"/tmp\"}})" }
                                                                        },
                                                                        "required": ["server", "method"]
                                                                      }
                                                                      """);

    /// <inheritdoc />
    public Result ValidateArguments(JsonElement args)
    {
        if (!args.TryGetProperty("server", out var sEl)
            || sEl.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(sEl.GetString()))
            return Result.Failure("Missing or empty 'server'.");

        if (!args.TryGetProperty("method", out var mEl)
            || mEl.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(mEl.GetString()))
            return Result.Failure("Missing or empty 'method'.");

        if (args.TryGetProperty("args", out var aEl) && aEl.ValueKind != JsonValueKind.Object)
            return Result.Failure("'args' must be a JSON object if present.");

        return Result.Success();
    }

    /// <inheritdoc />
    public async Task<ToolResult> ExecuteAsync(
        JsonElement args,
        ToolContext context,
        CancellationToken cancellationToken = default)
    {
        string server = args.GetProperty("server").GetString()!;
        string method = args.GetProperty("method").GetString()!;
        var methodArgs = args.TryGetProperty("args", out var a) && a.ValueKind == JsonValueKind.Object
            ? a
            : default;

        var registry = _registry;
        if (registry is null && context.Services is not null)
        {
            registry = context.Services.GetService<IMcpRegistry>();
        }

        if (registry is null)
        {
            return ToolResult.Error(
                "No IMcpRegistry is registered in the DI container. " +
                "Register one with services.AddSingleton<IMcpRegistry>(...) " +
                "and call registry.Register(name, stdioCmd) for each MCP server.");
        }

        _logger.LogDebug("MCP call: server={Server} method={Method}", server, method);

        // ROP-A Z1 п.17: boundary Match — the layer-edge deployment of a
        // Result into the tool's ToolResult contract.
        return await registry.InvokeAsync(server, method, methodArgs, cancellationToken)
            .Match(
                payload => ToolResult.Success(
                    $"MCP {server}.{method} →\n{payload}",
                    new { server, method, chars = payload.Length }),
                error => ToolResult.Error(
                    $"MCP call failed (server='{server}', method='{method}'): {error}",
                    new { server, method }))
            .ConfigureAwait(false);
    }
}
