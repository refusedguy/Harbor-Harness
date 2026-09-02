# python-hello — Python MCP stdio server

A minimal [Model Context Protocol](https://modelcontextprotocol.io) server written in
Python 3 (standard library only — `json` + `sys`, no third-party deps).

It speaks standard MCP JSON-RPC 2.0 over newline-delimited JSON on **stdin/stdout**
and logs to **stderr**. It exposes a single `echo` tool:

`tools/call` → `{ "name": "echo", "arguments": { "text": "hi" } }`
returns `{ "content": [ { "type": "text", "text": "hi" } ] }`.

## Register with Harbor

Add this to `~/.harbor/mcp.json` (or `<project>/.harbor/mcp.json`):

```json
{
  "mcpServers": {
    "python-hello": {
      "command": "python3",
      "args": ["main.py"],
      "cwd": "${projectRoot}/samples/mcp/python-hello"
    }
  }
}
```

Then the tool is callable like any builtin `ITool`:

```
mcp server=python-hello method=tools/call args={"name":"echo","arguments":{"text":"hello"}}
```

## Run standalone (for debugging)

```bash
echo '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{}}' | python3 main.py
```
