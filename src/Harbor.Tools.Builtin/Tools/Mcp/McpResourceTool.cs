using Harbor.Abstractions.Extensions;
using Harbor.Tools.Mcp;
using Microsoft.Extensions.Logging;
using Result = CSharpFunctionalExtensions.Result;

namespace Harbor.Tools.Builtin;
/// <summary>
///     Reads an MCP resource (<c>resources/read</c>) from a registered server
///     and returns its text contents. URIs come from <c>resources/list</c>
///     (via the generic <c>mcp</c> tool); this tool wraps the read path with a
///     fixed schema so the model doesn't have to hand-craft JSON-RPC params.
/// </summary>
[ToolInfo(
    "read_mcp_resource",
    "MCP Resource",
    "Read a resource from a registered Model Context Protocol server by URI. List available URIs first with the mcp tool (method 'resources/list'). Returns the resource text contents.",
    PromptSnippet = "read_mcp_resource: Read an MCP server resource by URI",
    PromptGuidelines = new[]
    {
        "Use `read_mcp_resource` for server-side data (docs, schemas, file views) exposed as MCP resources",
        "Discover URIs with mcp {\"method\":\"resources/list\"} before reading",
        "Binary (base64 blob) resources are rejected — ask for a text view instead",
    })]
[ToolSchema]
[ToolParam("server", "string", Description = "Registered MCP server name", Required = true)]
[ToolParam("uri", "string", Description = "Resource URI from resources/list (e.g. 'file:///docs/api.md')", Required = true)]
public sealed partial class McpResourceTool : ITool
{
    private readonly ILogger<McpResourceTool> _logger;
    private readonly IMcpRegistry? _registry;

    /// <summary>
    ///     Construct a <see cref="McpResourceTool" /> with no registry: every call fails with
    ///     an actionable error (#470 — the registry is constructor-injected by the
    ///     composition root; the per-call <c>ToolContext.Services</c> lookup it used
    ///     to fall back to always received <c>null!</c>).
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
    public ToolSafetyProfile SafetyProfile => ToolSafetyProfile.Opaque;

    /// <inheritdoc />
    public Result ValidateArguments(JsonElement args)
    {
        Result<string> server = JsonArgValidator.RequiredString(args, "server", "Missing or empty 'server'.");
        if (server.IsFailure)
            return server;

        return JsonArgValidator.RequiredString(args, "uri", "Missing or empty 'uri'.");
    }

    /// <inheritdoc />
    public async Task<ToolResult> ExecuteAsync(
        JsonElement args,
        ToolContext context,
        CancellationToken cancellationToken = default)
    {
        string server = args.GetProperty("server").GetString()!;
        string uri = args.GetProperty("uri").GetString()!;

        // #470: constructor-injected by the composition root. The per-call
        // lookup on the old service-provider field could never fire — the
        // agent loop handed every tool a null provider.
        var registry = _registry;

        if (registry is null)
        {
            return ToolResult.Error(
                "No IMcpRegistry was injected into the 'read_mcp_resource' tool. " +
                "The composition root builds it with services.AddSingleton<IMcpRegistry>(...) " +
                "and calls registry.Register(name, stdioCmd) for each MCP server.");
        }

        McpResourceToolLog.ReadingResource(_logger, server, uri);

        JsonElement paramsEl = McpJsonRpc.BuildResourceReadParams(uri);
        var invoked = await registry.InvokeAsync(server, "resources/read", paramsEl, cancellationToken)
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
            // Same DOM as JsonDocument.Parse, but through the source-generated
            // JsonElement info (PERF-002; TRIM-SERIALIZATION TypeInfo spelling).
            JsonElement root = JsonSerializer.Deserialize(payloadJson, McpJsonSerializerContext.Default.JsonElement);
            if (!root.TryGetProperty("contents", out var contents)
                || contents.ValueKind != JsonValueKind.Array
                || contents.GetArrayLength() == 0)
                return Result.Failure<string>($"MCP server returned no contents for '{uri}'.");

            // §PERF-006: per-call concat buffer rented. Synchronous, no
            // callback can touch it; early returns dispose via `using`.
            using var sb = StringBuilderPool.Rent(512);
            var b = sb.Builder;
            foreach (var item in contents.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                    continue;
                if (item.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String)
                {
                    if (b.Length > 0)
                        b.Append('\n');
                    b.Append(text.GetString());
                }
                else if (item.TryGetProperty("blob", out _))
                {
                    return Result.Failure<string>(
                        $"Resource '{uri}' is binary (base64 blob) — request a text view instead.");
                }
            }

            if (b.Length == 0)
                return Result.Failure<string>($"MCP server returned no text contents for '{uri}'.");
            return Result.Success(b.ToString());
        }
        catch (JsonException ex)
        {
            return Result.Failure<string>($"MCP server returned malformed resources/read payload: {ex.Message}");
        }
    }
}

/// <summary>
///     SG1: BCL <c>[LoggerMessage]</c> delegates for <see cref="McpResourceTool" />.
///     Templates, levels and operands are 1-to-1 with the former <c>LogX</c> calls.
/// </summary>
internal static partial class McpResourceToolLog
{
    [LoggerMessage(EventId = 1, Level = LogLevel.Debug, Message = "MCP resource read: server={Server} uri={Uri}")]
    public static partial void ReadingResource(ILogger logger, string server, string uri);
}
