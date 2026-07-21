        # Harbor.Tools.Tree

        Part of the Harbor tool split — one of 14 leaf tool projects extracted out of the
        old `Harbor.Tools.Builtin` god-project. The `Harbor.Tools.Builtin` project remains
        as a thin facade that references all 14 leaves so existing consumers keep compiling
        without code changes.

        ## What it does

        `tree` — Renders an ASCII directory tree. Respects .gitignore when `git` is available; otherwise falls back to a built-in heavy-dir prune list. Caps depth and entry count to keep output bounded.

        ## Args schema

        | Field | Type | Description |

|-------|------|-------------|
| `path` | string | Required. Root directory. |
| `maxDepth` | integer | Optional. Default 3, hard cap 10. |
| `maxEntries` | integer | Optional. Default 1000, hard cap 10 000. |

        ## Example usage

        ```json

{"path":".","maxDepth":4}

```

        ## Dependencies

        Harbor.Abstractions only (uses StringBuilderPool from Harbor.Abstractions.Extensions).

        ## Permission rules

        Read-only. Tries `git ls-files` first (4-second timeout) to honour .gitignore; on failure falls back to heavy-dir prune list.

        ## See also

        - `docs/TOOLS_CATALOG.md` — full builtin tool catalogue.
        - `docs/ARCHITECTURE_LAYERS.md` — Clean Architecture layer rules (this project is
          Infrastructure; references Domain only).
        - `src/Harbor.Tools.Builtin/Harbor.Tools.Builtin.csproj` — facade that aggregates
          all 14 leaf tool projects.
