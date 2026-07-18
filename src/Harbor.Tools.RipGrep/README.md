        # Harbor.Tools.RipGrep

        Part of the Harbor tool split — one of 14 leaf tool projects extracted out of the
        old `Harbor.Tools.Builtin` god-project. The `Harbor.Tools.Builtin` project remains
        as a thin facade that references all 14 leaves so existing consumers keep compiling
        without code changes.

        ## What it does

        `rg` — Wraps the `rg` (ripgrep) binary for fast content search. Falls back to a helpful hint when `rg` is not on PATH (does not silently fall through to GrepTool).

        ## Args schema

        | Field | Type | Description |
|-------|------|-------------|
| `pattern` | string | Required. Regex pattern. |
| `path` | string | Optional. Root directory. Defaults to cwd. |
| `maxResults` | integer | Optional. Default 100, hard cap 10 000. |
| `contextChars` | integer | Optional. Default 400. |

        ## Example usage

        ```json
{"pattern":"private\\s+void","path":"src","maxResults":50}
```

        ## Dependencies

        Harbor.Abstractions only. Wraps the external `rg` binary — `rg` must be on PATH at runtime (no NuGet dependency).

        ## Permission rules

        Read-only. 30-second subprocess timeout. Returns a clear error if `rg` is not installed rather than silently degrading.

        ## See also

        - `docs/TOOLS_CATALOG.md` — full builtin tool catalogue.
        - `docs/ARCHITECTURE_LAYERS.md` — Clean Architecture layer rules (this project is
          Infrastructure; references Domain only).
        - `src/Harbor.Tools.Builtin/Harbor.Tools.Builtin.csproj` — facade that aggregates
          all 14 leaf tool projects.
