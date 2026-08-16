        # Harbor.Tools.Mcp

        Part of the Harbor tool split — one of 14 leaf tool projects extracted out of the
        old `Harbor.Tools.Builtin` god-project. The `Harbor.Tools.Builtin` project remains
        as a thin facade that references all 14 leaves so existing consumers keep compiling
        without code changes.

        ## What it does

        `mcp` — Bridge to Model Context Protocol (MCP) servers. The agent calls a named server's method via the IMcpRegistry; the registry looks up the server, transports the JSON-RPC call, and returns the response payload as a string.

        ## Args schema

        | Field | Type | Description |

|-------|------|-------------|
| `server` | string | Required. Registered MCP server name. |
| `method` | string | Required. JSON-RPC method name. |
| `params` | object | Optional. JSON-RPC params object. |
| `timeout` | integer | Optional. Seconds. Default 30. |

        ## Example usage

        ```json

{"server":"filesystem","method":"read_file","params":{"path":"/tmp/a.txt"}}

```

        ## Dependencies

        Harbor.Abstractions only. Resolves IMcpRegistry at runtime via constructor injection or ToolContext.Services. The InMemoryMcpRegistry implementation lives in Harbor.Core and is registered by the host.

        ## Permission rules

        Sequential. Server must be registered first via IMcpRegistry.Register(name, stdioCmd). Returns a clear error if no registry is wired.

        ## See also

        - `docs/TOOLS_CATALOG.md` — full builtin tool catalogue.
        - `docs/ARCHITECTURE_LAYERS.md` — Clean Architecture layer rules (this project is
          Infrastructure; references Domain only).
        - `src/Harbor.Tools.Builtin/Harbor.Tools.Builtin.csproj` — facade that aggregates
          all 14 leaf tool projects.
