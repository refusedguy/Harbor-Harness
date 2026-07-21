        # Harbor.Tools.Ls

        Part of the Harbor tool split — one of 14 leaf tool projects extracted out of the
        old `Harbor.Tools.Builtin` god-project. The `Harbor.Tools.Builtin` project remains
        as a thin facade that references all 14 leaves so existing consumers keep compiling
        without code changes.

        ## What it does

        `ls` — Lists directory contents (type, size, mtime). Caps output; prunes heavy dirs when recursive.

        ## Args schema

        | Field | Type | Description |

|-------|------|-------------|
| `path` | string | Required. Directory path. |
| `recursive` | bool | Optional. Default false. |
| `maxDepth` | integer | Optional. Default 3, hard cap 10. |
| `maxEntries` | integer | Optional. Default 500, hard cap 2000. |

        ## Example usage

        ```json

{"path":".","recursive":false}

```

        ## Dependencies

        Harbor.Abstractions only.

        ## Permission rules

        Read-only. Same heavy-dir prune list as Grep/Glob. 2000 entry hard cap to keep model context bounded.

        ## See also

        - `docs/TOOLS_CATALOG.md` — full builtin tool catalogue.
        - `docs/ARCHITECTURE_LAYERS.md` — Clean Architecture layer rules (this project is
          Infrastructure; references Domain only).
        - `src/Harbor.Tools.Builtin/Harbor.Tools.Builtin.csproj` — facade that aggregates
          all 14 leaf tool projects.
