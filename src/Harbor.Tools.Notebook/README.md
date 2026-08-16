        # Harbor.Tools.Notebook

        Part of the Harbor tool split — one of 14 leaf tool projects extracted out of the
        old `Harbor.Tools.Builtin` god-project. The `Harbor.Tools.Builtin` project remains
        as a thin facade that references all 14 leaves so existing consumers keep compiling
        without code changes.

        ## What it does

        `notebook` — Persistent per-session markdown notes. The agent can stash small bits of context (file paths, decisions, intermediate findings) and pull them back later. Notes are stored as JSON in ~/.harbor/notes/<sessionId>.json.

        ## Args schema

        | Field | Type | Description |

|-------|------|-------------|
| `action` | string | Required. One of: set/get/add/clear/list. |
| `key` | string | Required for set/get/add/clear. |
| `value` | string | Required for set/add. |

        ## Example usage

        ```json

{"action":"set","key":"decision-1","value":"Use sqlite for sessions"}

```

        ## Dependencies

        Harbor.Abstractions only (uses StringBuilderPool from Harbor.Abstractions.Extensions).

        ## Permission rules

        Mutating (under ~/.harbor/notes/<sessionId>.json). 16 384 char per-note cap, 128 char key cap, 256 notes per session.

        ## See also

        - `docs/TOOLS_CATALOG.md` — full builtin tool catalogue.
        - `docs/ARCHITECTURE_LAYERS.md` — Clean Architecture layer rules (this project is
          Infrastructure; references Domain only).
        - `src/Harbor.Tools.Builtin/Harbor.Tools.Builtin.csproj` — facade that aggregates
          all 14 leaf tool projects.
