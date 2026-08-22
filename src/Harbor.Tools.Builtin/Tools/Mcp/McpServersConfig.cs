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
}

/// <summary>
///     Root of an mcp.json file. Only the <c>mcpServers</c> map is read; everything else is ignored.
/// </summary>
public sealed class McpServersConfig
{
    [JsonPropertyName("mcpServers")]
    public Dictionary<string, McpServerConfig>? McpServers { get; set; }
}
