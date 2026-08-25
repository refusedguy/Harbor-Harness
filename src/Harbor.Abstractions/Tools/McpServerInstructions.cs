namespace Harbor.Abstractions.Tools;
/// <summary>
///     Instructions advertised by one connected MCP server (the
///     <c>instructions</c> field of the JSON-RPC <c>initialize</c> result, or a
///     static hint from <c>mcp.json</c>).
/// </summary>
/// <param name="ServerName">Stable registry name of the server (e.g. <c>filesystem</c>).</param>
/// <param name="Instructions">Non-empty server-provided instruction text.</param>
public sealed record McpServerInstructions(string ServerName, string Instructions);
