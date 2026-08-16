        # Harbor.Tools.Glob

        Part of the Harbor tool split — one of 14 leaf tool projects extracted out of the
        old `Harbor.Tools.Builtin` god-project. The `Harbor.Tools.Builtin` project remains
        as a thin facade that references all 14 leaves so existing consumers keep compiling
        without code changes.

        ## What it does

        `glob` — Find files by glob. Supports **, *, ?, and simple *.{a,b} braces. Prunes heavy dirs (not a full .gitignore parser).

        ## Args schema

        | Field | Type | Description |

|-------|------|-------------|
| `pattern` | string | Required. Glob pattern. |
| `path` | string | Optional. Root directory. Defaults to cwd. |
| `maxResults` | integer | Optional. Default 1000, hard cap 5000. |

        ## Example usage

        ```json

{"pattern":"**/*.cs","path":"src"}

```

        ## Dependencies

        Harbor.Abstractions only.

        ## Permission rules

        Read-only. Prunes .git, node_modules, bin, obj, dist, build, out, target, vendor, __pycache__, .next, .nuxt, etc.

        ## See also

        - `docs/TOOLS_CATALOG.md` — full builtin tool catalogue.
        - `docs/ARCHITECTURE_LAYERS.md` — Clean Architecture layer rules (this project is
          Infrastructure; references Domain only).
        - `src/Harbor.Tools.Builtin/Harbor.Tools.Builtin.csproj` — facade that aggregates
          all 14 leaf tool projects.
