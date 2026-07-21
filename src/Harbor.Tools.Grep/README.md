        # Harbor.Tools.Grep

        Part of the Harbor tool split — one of 14 leaf tool projects extracted out of the
        old `Harbor.Tools.Builtin` god-project. The `Harbor.Tools.Builtin` project remains
        as a thin facade that references all 14 leaves so existing consumers keep compiling
        without code changes.

        ## What it does

        `grep` — Fast recursive content search. Local-disk: sync bulk I/O + dir prune + binary skip. Parallel across files; stops at maxResults.

        ## Args schema

        | Field | Type | Description |

|-------|------|-------------|
| `pattern` | string | Required. Regex pattern. |
| `path` | string | Optional. Root directory. Defaults to cwd. |
| `include` | string | Optional. Glob filter. |
| `maxResults` | integer | Optional. Default 100. |

        ## Example usage

        ```json

{"pattern":"TODO\\([^)]+\\)","path":"src","include":"*.cs"}

```

        ## Dependencies

        Harbor.Abstractions only.

        ## Permission rules

        Read-only. Prunes .git, node_modules, bin, obj, etc. Skips files >2 MiB. Binary files skipped via NUL-byte probe.

        ## See also

        - `docs/TOOLS_CATALOG.md` — full builtin tool catalogue.
        - `docs/ARCHITECTURE_LAYERS.md` — Clean Architecture layer rules (this project is
          Infrastructure; references Domain only).
        - `src/Harbor.Tools.Builtin/Harbor.Tools.Builtin.csproj` — facade that aggregates
          all 14 leaf tool projects.
