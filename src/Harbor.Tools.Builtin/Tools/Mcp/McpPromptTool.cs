using Harbor.Tools.Mcp;
using Microsoft.Extensions.Logging;
using System.Text;
using Result = CSharpFunctionalExtensions.Result;

namespace Harbor.Tools.Builtin;
/// <summary>
///     Renders an MCP prompt (<c>prompts/get</c>) from a registered server into
///     plain text the agent can follow. Prompt names come from
///     <c>prompts/list</c> (via the generic <c>mcp</c> tool); this tool wraps
///     the get path with a fixed schema so the model doesn't have to
///     hand-craft JSON-RPC params. (Native slash-command surfacing stays
///     future work — the tool output flows into the loop the same way.)
/// </summary>
public sealed class McpPromptTool : ITool
{
    private readonly ILogger<McpPromptTool> _logger;
    private readonly IMcpRegistry? _registry;

    /// <summary>
    ///     Construct a <see cref="McpPromptTool" /> with no registry: every call fails with
    ///     an actionable error (#470 — the registry is constructor-injected by the
    ///     composition root; the per-call <c>ToolContext.Services</c> lookup it used
    ///     to fall back to always received <c>null!</c>).
    /// </summary>
    /// <param name="logger">Logger for diagnostics.</param>
    public McpPromptTool(ILogger<McpPromptTool> logger)
    {
        _logger = logger;
    }

    /// <summary>
    ///     Construct a <see cref="McpPromptTool" /> with a fixed registry (used in tests
    ///     where the DI container is not configured).
    /// </summary>
    /// <param name="registry">The MCP registry to use.</param>
    /// <param name="logger">Logger for diagnostics.</param>
    public McpPromptTool(IMcpRegistry registry, ILogger<McpPromptTool> logger) : this(logger)
    {
        _registry = registry;
    }

    /// <inheritdoc />
    public ToolName Name => ToolName.Create("mcp_prompt");

    /// <inheritdoc />
    public string DisplayName => "MCP Prompt";

    /// <inheritdoc />
    public string Description =>
        "Render a prompt from a registered Model Context Protocol server. " +
        "List available prompts first with the mcp tool (method 'prompts/list'). " +
        "Returns the prompt messages as plain text to follow.";

    /// <inheritdoc />
    public ExecutionMode ExecutionMode => ExecutionMode.Parallel;

    /// <inheritdoc />
    public string? PromptSnippet => "mcp_prompt: Render an MCP server prompt by name";

    /// <inheritdoc />
    public IReadOnlyList<string> PromptGuidelines { get; } =
    [
        "Use `mcp_prompt` to expand a server-side prompt template (review checklist, commit message, …)",
        "Discover prompt names with mcp {\"method\":\"prompts/list\"} before rendering",
        "Pass template variables through `arguments` — check the prompt's required arguments first",
    ];

    /// <inheritdoc />
    public JsonDocument ParameterSchema { get; } = JsonDocument.Parse("""
                                                                      {
                                                                        "type": "object",
                                                                        "properties": {
                                                                          "server":    { "type": "string", "description": "Registered MCP server name" },
                                                                          "name":      { "type": "string", "description": "Prompt name from prompts/list" },
                                                                          "arguments": { "type": "object", "description": "Template variables (optional)" }
                                                                        },
                                                                        "required": ["server", "name"]
                                                                      }
                                                                      """);

    /// <inheritdoc />
    public Result ValidateArguments(JsonElement args)
    {
        Result<string> server = JsonArgValidator.RequiredString(args, "server", "Missing or empty 'server'.");
        if (server.IsFailure)
            return server;

        Result<string> name = JsonArgValidator.RequiredString(args, "name", "Missing or empty 'name'.");
        if (name.IsFailure)
            return name;

        return JsonArgValidator.OptionalObject(args, "arguments", "'arguments' must be a JSON object if present.");
    }

    /// <inheritdoc />
    public async Task<ToolResult> ExecuteAsync(
        JsonElement args,
        ToolContext context,
        CancellationToken cancellationToken = default)
    {
        string server = args.GetProperty("server").GetString()!;
        string name = args.GetProperty("name").GetString()!;
        // #180: writer-built params over a pooled buffer. Absent arguments
        // default to {} exactly as before (previously via the "{}" fallback).
        JsonElement arguments = args.TryGetProperty("arguments", out var a) && a.ValueKind == JsonValueKind.Object
            ? a
            : default;

        // #470: constructor-injected by the composition root. The per-call
        // lookup on the old service-provider field could never fire — the
        // agent loop handed every tool a null provider.
        var registry = _registry;

        if (registry is null)
        {
            return ToolResult.Error(
                "No IMcpRegistry was injected into the 'mcp_prompt' tool. " +
                "The composition root builds it with services.AddSingleton<IMcpRegistry>(...) " +
                "and calls registry.Register(name, stdioCmd) for each MCP server.");
        }

        _logger.LogDebug("MCP prompt render: server={Server} name={Name}", server, name);

        using var paramsDoc = McpJsonRpc.BuildPromptGetParams(name, arguments);
        var invoked = await registry.InvokeAsync(server, "prompts/get", paramsDoc.RootElement, cancellationToken)
            .ConfigureAwait(false);
        if (invoked.IsFailure)
        {
            return ToolResult.Error(
                $"MCP prompt render failed (server='{server}', name='{name}'): {invoked.Error}",
                new { server, name });
        }

        var extracted = ExtractText(invoked.Value, name);
        return extracted.IsFailure
            ? ToolResult.Error(extracted.Error, new { server, name })
            : ToolResult.Success(extracted.Value, new { server, name, chars = extracted.Value.Length });
    }

    /// <summary>
    ///     Pull text out of a <c>prompts/get</c> result payload:
    ///     <c>{description?,messages:[{role,content:{type:text,text}|...}]}</c>.
    ///     Non-text content parts (images, embedded resources) are noted and
    ///     skipped — the agent works from the prose.
    /// </summary>
    internal static Result<string> ExtractText(string payloadJson, string name)
    {
        try
        {
            using var doc = JsonDocument.Parse(payloadJson);
            if (!doc.RootElement.TryGetProperty("messages", out var messages)
                || messages.ValueKind != JsonValueKind.Array
                || messages.GetArrayLength() == 0)
                return Result.Failure<string>($"MCP server returned no messages for prompt '{name}'.");

            var sb = new StringBuilder();
            int skipped = 0;
            foreach (var message in messages.EnumerateArray())
            {
                if (message.ValueKind != JsonValueKind.Object)
                    continue;
                string role = message.TryGetProperty("role", out var r) && r.ValueKind == JsonValueKind.String
                    ? r.GetString()!
                    : "assistant";
                if (!message.TryGetProperty("content", out var content)
                    || content.ValueKind != JsonValueKind.Object)
                {
                    skipped++;
                    continue;
                }

                string? text = content.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String
                    ? t.GetString()
                    : null;
                if (text is null)
                {
                    skipped++;
                    continue;
                }

                if (sb.Length > 0)
                    sb.Append("\n\n");
                sb.Append('[').Append(role).Append("]\n").Append(text);
            }

            if (sb.Length == 0)
                return Result.Failure<string>($"MCP server returned no text messages for prompt '{name}'.");
            if (skipped > 0)
                sb.Append($"\n\n({skipped} non-text message part(s) omitted.)");
            return Result.Success(sb.ToString());
        }
        catch (JsonException ex)
        {
            return Result.Failure<string>($"MCP server returned malformed prompts/get payload: {ex.Message}");
        }
    }
}
