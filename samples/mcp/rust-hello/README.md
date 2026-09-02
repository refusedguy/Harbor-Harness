# rust-hello — Rust MCP stdio server

A minimal [Model Context Protocol](https://modelcontextprotocol.io) server written in
Rust (`serde` + `serde_json` only).

It speaks standard MCP JSON-RPC 2.0 over newline-delimited JSON on **stdin/stdout**
and logs to **stderr**. It exposes a single `echo` tool.

## Register with Harbor

Build it first, then point `mcp.json` at the binary:

```bash
cd samples/mcp/rust-hello
cargo build --release
```

```json
{
  "mcpServers": {
    "rust-hello": {
      "command": "${projectRoot}/samples/mcp/rust-hello/target/release/rust_hello"
    }
  }
}
```

Then the tool is callable like any builtin `ITool`:

```
mcp server=rust-hello method=tools/call args={"name":"echo","arguments":{"text":"hello"}}
```

## Run standalone (for debugging)

```bash
echo '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{}}' | cargo run
```
