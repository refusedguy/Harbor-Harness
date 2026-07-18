        # Harbor.Tools.Write

        Part of the Harbor tool split — one of 14 leaf tool projects extracted out of the
        old `Harbor.Tools.Builtin` god-project. The `Harbor.Tools.Builtin` project remains
        as a thin facade that references all 14 leaves so existing consumers keep compiling
        without code changes.

        ## What it does

        `write` — Writes/overwrites a text file. Creates parent directories by default. Sequential execution mode.

        ## Args schema

        | Field | Type | Description |
|-------|------|-------------|
| `path` | string | Required. Target file path. |
| `content` | string | Required. UTF-8 text to write. |
| `createDirs` | bool | Optional. Default true. |

        ## Example usage

        ```json
{"path":"notes.txt","content":"hello world"}
```

        ## Dependencies

        Harbor.Abstractions only.

        ## Permission rules

        Mutating. Should be subject to the host's PermissionRuleset (write paths under cwd by default). 5 000 000 char cap.

        ## See also

        - `docs/TOOLS_CATALOG.md` — full builtin tool catalogue.
        - `docs/ARCHITECTURE_LAYERS.md` — Clean Architecture layer rules (this project is
          Infrastructure; references Domain only).
        - `src/Harbor.Tools.Builtin/Harbor.Tools.Builtin.csproj` — facade that aggregates
          all 14 leaf tool projects.
