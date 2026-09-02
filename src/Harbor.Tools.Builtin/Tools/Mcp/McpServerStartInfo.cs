namespace Harbor.Tools.Mcp;

/// <summary>
///     How to spawn an MCP stdio server. Plain process-start description — no Harbor-specific
///     protocol, no driver manifest. The process is expected to speak standard MCP over stdio.
/// </summary>
public sealed class McpServerStartInfo
{
    public required string Command { get; init; }
    public IReadOnlyList<string> Args { get; init; } = [];
    public string? WorkingDirectory { get; init; }
    public IReadOnlyDictionary<string, string>? Environment { get; init; }
}
