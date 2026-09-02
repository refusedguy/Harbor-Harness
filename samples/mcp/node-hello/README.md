# node-hello — Node.js MCP stdio server

A minimal [Model Context Protocol](https://modelcontextprotocol.io) server written in
Node.js (standard library only — `process.stdin`/`process.stdout`, no npm deps).

It speaks standard MCP JSON-RPC 2.0 over newline-delimited JSON on **stdin/stdout**
and logs to **stderr**. It exposes a single `echo` tool:

`tools/call` → `{ "name": "echo", "arguments": { "text": "hi" } }`
returns `{ "content": [ { "type": "text", "text": "hi" } ] }`.

## Register with Harbor

Add this to `~/.harbor/mcp.json` (or `<project>/.harbor/mcp.json`):

```json
{
  "mcpServers": {
    "node-hello": {
      "command": "node",
      "args": ["index.js"],
      "cwd": "${projectRoot}/samples/mcp/node-hello"
    }
  }
}
```

Then the tool is callable like any builtin `ITool`:

```
mcp server=node-hello method=tools/call args={"name":"echo","arguments":{"text":"hello"}}
```

## Run standalone (for debugging)

```bash
echo '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{}}' | node index.js
```
