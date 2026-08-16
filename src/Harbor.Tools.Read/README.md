        # Harbor.Tools.Read

        Part of the Harbor tool split — one of 14 leaf tool projects extracted out of the
        old `Harbor.Tools.Builtin` god-project. The `Harbor.Tools.Builtin` project remains
        as a thin facade that references all 14 leaves so existing consumers keep compiling
        without code changes.

        ## What it does

        `read` — Reads a text file (optionally a line window) or reports image metadata. Streams lines for offset/limit — never loads whole multi-MB files just to slice.

        ## Args schema

        | Field | Type | Description |

|-------|------|-------------|
| `path` | string | Required. Absolute or relative file path. |
| `offset` | integer | Optional. 1-based start line. |
| `limit` | integer | Optional. Max lines to return. |

        ## Example usage

        ```json

{"path":"src/Program.cs","offset":50,"limit":20}

```

        ## Dependencies

        Harbor.Abstractions only.

        ## Permission rules

        Read-only. Refuses binary non-image files. Hard caps: 10 MiB text, 20 MiB image, 100 000 chars per call.

        ## See also

        - `docs/TOOLS_CATALOG.md` — full builtin tool catalogue.
        - `docs/ARCHITECTURE_LAYERS.md` — Clean Architecture layer rules (this project is
          Infrastructure; references Domain only).
        - `src/Harbor.Tools.Builtin/Harbor.Tools.Builtin.csproj` — facade that aggregates
          all 14 leaf tool projects.
