using System.Text.Json.Serialization;

namespace Harbor.Tools.Mcp;

/// <summary>
///     A single MCP server entry from an mcp.json file (industry-standard schema).
///     Unknown fields are ignored (forward-compatible).
/// </summary>
public sealed class McpServerConfig
{
    [JsonPropertyName("command")]
    public string? Command { get; set; }

    [JsonPropertyName("args")]
    public IReadOnlyList<string>? Args { get; set; }

    [JsonPropertyName("cwd")]
    public string? Cwd { get; set; }

    [JsonPropertyName("env")]
    public Dictionary<string, string>? Env { get; set; }

    [JsonPropertyName("disabled")]
    public bool? Disabled { get; set; }

    /// <summary>Remote endpoint URL (Harbor extension; alternatively spawn via command).</summary>
    [JsonPropertyName("url")]
    public string? Url { get; set; }

    /// <summary>
    ///     Remote transport kind — any name a registered
    ///     <see cref="IMcpTransportFactory" /> claims (<c>"http"</c> / <c>"sse"</c>
    ///     out of the box). An unknown name is rejected at registration, never
    ///     defaulted to another transport (#477).
    /// </summary>
    [JsonPropertyName("transport")]
    public string? Transport { get; set; }

    /// <summary>Extra headers for remote endpoints (Harbor extension).</summary>
    [JsonPropertyName("headers")]
    public Dictionary<string, string>? Headers { get; set; }

    /// <summary>OAuth2 settings for remote endpoints (Harbor extension; parsed manually).</summary>
    public McpOAuthConfig? OAuth { get; set; }
}

/// <summary>
///     Root of an mcp.json file. Only the <c>mcpServers</c> map is read; everything else is ignored.
/// </summary>
public sealed class McpServersConfig
{
    [JsonPropertyName("mcpServers")]
    public Dictionary<string, McpServerConfig>? McpServers { get; set; }
}
