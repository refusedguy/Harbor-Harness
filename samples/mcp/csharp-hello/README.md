# csharp-hello — C# MCP stdio server

A minimal [Model Context Protocol](https://modelcontextprotocol.io) server written in
C# (single `.cs` file, `System.Text.Json` only).

It speaks standard MCP JSON-RPC 2.0 over newline-delimited JSON on **stdin/stdout**
and logs to **stderr**. It exposes a single `echo` tool.

Harbor does **not** compile or load this as a Roslyn plugin — it is a normal MCP
process. Harbor just spawns the command from `mcp.json` and speaks JSON-RPC to it.

## Register with Harbor

Add this to `~/.harbor/mcp.json` (or `<project>/.harbor/mcp.json`):

```json
{
  "mcpServers": {
    "csharp-hello": {
      "command": "dotnet",
      "args": ["run", "--file", "Server.cs"],
      "cwd": "${projectRoot}/samples/mcp/csharp-hello"
    }
  }
}
```

Then the tool is callable like any builtin `ITool`:

```
mcp server=csharp-hello method=tools/call args={"name":"echo","arguments":{"text":"hello"}}
```

## Run standalone (for debugging)

```bash
echo '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{}}' | dotnet run --file Server.cs
```
