using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Text;
using Result = CSharpFunctionalExtensions.Result;

namespace Harbor.Tools.Builtin;
/// <summary>
///     Reads an MCP resource (<c>resources/read</c>) from a registered server
///     and returns its text contents. URIs come from <c>resources/list</c>
///     (via the generic <c>mcp</c> tool); this tool wraps the read path with a
///     fixed schema so the model doesn't have to hand-craft JSON-RPC params.
/// </summary>
public sealed class McpResourceTool : ITool
{
    private readonly ILogger<McpResourceTool> _logger;
    private readonly IMcpRegistry? _registry;

    /// <summary>
    ///     Construct a <see cref="McpResourceTool" /> that resolves the registry from
    ///     <see cref="ToolContext.Services" /> on each call (preferred for DI).
    /// </summary>
    /// <param name="logger">Logger for diagnostics.</param>
    public McpResourceTool(ILogger<McpResourceTool> logger)
    {
        _logger = logger;
    }

    /// <summary>
    ///     Construct a <see cref="McpResourceTool" /> with a fixed registry (used in tests
    ///     where the DI container is not configured).
    /// </summary>
    /// <param name="registry">The MCP registry to use.</param>
    /// <param name="logger">Logger for diagnostics.</param>
    public McpResourceTool(IMcpRegistry registry, ILogger<McpResourceTool> logger) : this(logger)
    {
        _registry = registry;
    }

    /// <inheritdoc />
    public ToolName Name => ToolName.Create("read_mcp_resource");

    /// <inheritdoc />
    public string DisplayName => "MCP Resource";

    /// <inheritdoc />
    public string Description =>
        "Read a resource from a registered Model Context Protocol server by URI. " +
        "List available URIs first with the mcp tool (method 'resources/list'). " +
        "Returns the resource text contents.";

    /// <inheritdoc />
    public ExecutionMode ExecutionMode => ExecutionMode.Parallel;

    /// <inheritdoc />
    public string? PromptSnippet => "read_mcp_resource: Read an MCP server resource by URI";

    /// <inheritdoc />
    public IReadOnlyList<string> PromptGuidelines { get; } =
    [
        "Use `read_mcp_resource` for server-side data (docs, schemas, file views) exposed as MCP resources",
        "Discover URIs with mcp {\"method\":\"resources/list\"} before reading",
        "Binary (base64 blob) resources are rejected — ask for a text view instead",
    ];

    /// <inheritdoc />
    public JsonDocument ParameterSchema { get; } = JsonDocument.Parse("""
                                                                      {
                                                                        "type": "object",
                                                                        "properties": {
                                                                          "server": { "type": "string", "description": "Registered MCP server name" },
                                                                          "uri":    { "type": "string", "description": "Resource URI from resources/list (e.g. 'file:///docs/api.md')" }
                                                                        },
                                                                        "required": ["server", "uri"]
                                                                      }
                                                                      """);

    /// <inheritdoc />
    public Result ValidateArguments(JsonElement args)
    {
        if (!args.TryGetProperty("server", out var sEl)
            || sEl.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(sEl.GetString()))
            return Result.Failure("Missing or empty 'server'.");

        if (!args.TryGetProperty("uri", out var uEl)
            || uEl.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(uEl.GetString()))
            return Result.Failure("Missing or empty 'uri'.");

        return Result.Success();
    }

    /// <inheritdoc />
    public async Task<ToolResult> ExecuteAsync(
        JsonElement args,
        ToolContext context,
        CancellationToken cancellationToken = default)
    {
        string server = args.GetProperty("server").GetString()!;
        string uri = args.GetProperty("uri").GetString()!;

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

        _logger.LogDebug("MCP resource read: server={Server} uri={Uri}", server, uri);

        using var paramsDoc = JsonDocument.Parse($"{{\"uri\":{JsonSerializer.Serialize(uri)}}}");
        var invoked = await registry.InvokeAsync(server, "resources/read", paramsDoc.RootElement.Clone(), cancellationToken)
            .ConfigureAwait(false);
        if (invoked.IsFailure)
        {
            return ToolResult.Error(
                $"MCP resource read failed (server='{server}', uri='{uri}'): {invoked.Error}",
                new { server, uri });
        }

        var extracted = ExtractText(invoked.Value, uri);
        return extracted.IsFailure
            ? ToolResult.Error(extracted.Error, new { server, uri })
            : ToolResult.Success(extracted.Value, new { server, uri, chars = extracted.Value.Length });
    }

    /// <summary>
    ///     Pull text out of a <c>resources/read</c> result payload:
    ///     <c>{contents:[{uri,mimeType?,text?}]}</c>. Blob (base64) entries are
    ///     rejected — the agent should request a text view instead.
    /// </summary>
    internal static Result<string> ExtractText(string payloadJson, string uri)
    {
        try
        {
            using var doc = JsonDocument.Parse(payloadJson);
            if (!doc.RootElement.TryGetProperty("contents", out var contents)
                || contents.ValueKind != JsonValueKind.Array
                || contents.GetArrayLength() == 0)
                return Result.Failure<string>($"MCP server returned no contents for '{uri}'.");

            var sb = new StringBuilder();
            foreach (var item in contents.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                    continue;
                if (item.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String)
                {
                    if (sb.Length > 0)
                        sb.Append('\n');
                    sb.Append(text.GetString());
                }
                else if (item.TryGetProperty("blob", out _))
                {
                    return Result.Failure<string>(
                        $"Resource '{uri}' is binary (base64 blob) — request a text view instead.");
                }
            }

            if (sb.Length == 0)
                return Result.Failure<string>($"MCP server returned no text contents for '{uri}'.");
            return Result.Success(sb.ToString());
        }
        catch (JsonException ex)
        {
            return Result.Failure<string>($"MCP server returned malformed resources/read payload: {ex.Message}");
        }
    }
}
