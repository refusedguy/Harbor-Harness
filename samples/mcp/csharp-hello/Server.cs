// Minimal Model Context Protocol (MCP) stdio server — C#, single file, stdlib only.
//
// Speaks standard MCP JSON-RPC 2.0 over newline-delimited JSON on stdin/stdout and logs
// to stderr. Exposes a single `echo` tool. Run with:  dotnet run --file Server.cs
//
// This is a complete, independent MCP server. Harbor does NOT compile or load it as a
// plugin — it simply spawns the process described in mcp.json.

using System.Text;
using System.Text.Json;

const string ProtocolVersion = "2024-11-05";

void Send(string json)
{
    Console.Out.Write(json);
    Console.Out.Write('\n');
    Console.Out.Flush();
}

string JsonId(JsonElement idEl) =>
    idEl.ValueKind == JsonValueKind.Number ? idEl.GetRawText()
    : idEl.ValueKind == JsonValueKind.String ? "\"" + idEl.GetString() + "\""
    : "null";

string Escape(string s) =>
    s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r");

string? line;
while ((line = Console.ReadLine()) is not null)
{
    if (string.IsNullOrWhiteSpace(line)) continue;

    JsonDocument doc;
    try { doc = JsonDocument.Parse(line); }
    catch (JsonException) { Console.Error.WriteLine("[csharp-hello] bad json"); continue; }

    using (doc)
    {
        var root = doc.RootElement;
        if (!root.TryGetProperty("method", out var methodEl) || methodEl.ValueKind != JsonValueKind.String)
            continue;

        var method = methodEl.GetString()!;
        bool hasId = root.TryGetProperty("id", out var idEl);
        var id = hasId ? JsonId(idEl) : "null";

        switch (method)
        {
            case "initialize":
                Send($"{{\"jsonrpc\":\"2.0\",\"id\":{id},\"result\":{{\"protocolVersion\":\"{ProtocolVersion}\",\"capabilities\":{{\"tools\":{{\"listChanged\":false}}}},\"serverInfo\":{{\"name\":\"csharp-hello\",\"version\":\"0.1.0\"}}}}}}");
                break;

            case "notifications/initialized":
                break;

            case "ping":
                if (hasId) Send($"{{\"jsonrpc\":\"2.0\",\"id\":{id},\"result\":{{}}}}");
                break;

            case "tools/list":
                Send($"{{\"jsonrpc\":\"2.0\",\"id\":{id},\"result\":{{\"tools\":[{{\"name\":\"echo\",\"description\":\"Echo the provided text back to the caller.\",\"inputSchema\":{{\"type\":\"object\",\"properties\":{{\"text\":{{\"type\":\"string\"}}}},\"required\":[\"text\"]}}}}]}}}}");
                break;

            case "tools/call":
                string text = "";
                if (root.TryGetProperty("params", out var p) && p.ValueKind == JsonValueKind.Object
                    && p.TryGetProperty("arguments", out var a) && a.ValueKind == JsonValueKind.Object
                    && a.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String)
                {
                    text = t.GetString()!;
                }
                if (hasId) Send($"{{\"jsonrpc\":\"2.0\",\"id\":{id},\"result\":{{\"content\":[{{\"type\":\"text\",\"text\":\"{Escape(text)}\"}}],\"isError\":false}}}}");
                break;

            default:
                if (hasId) Send($"{{\"jsonrpc\":\"2.0\",\"id\":{id},\"error\":{{\"code\":-32601,\"message\":\"Method not found: {method}\"}}}}");
                break;
        }
    }
}
