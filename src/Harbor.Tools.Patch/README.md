        # Harbor.Tools.Patch

        Part of the Harbor tool split — one of 14 leaf tool projects extracted out of the
        old `Harbor.Tools.Builtin` god-project. The `Harbor.Tools.Builtin` project remains
        as a thin facade that references all 14 leaves so existing consumers keep compiling
        without code changes.

        ## What it does

        `patch` — Applies a unified-diff patch to a single file. Validates context lines match before applying; writes to a temp file and renames atomically. Returns a compact preview of what changed.

        ## Args schema

        | Field | Type | Description |

|-------|------|-------------|
| `path` | string | Required. Target file path. |
| `patch` | string | Required. Unified-diff body. |

        ## Example usage

        ```json

{"path":"src/Foo.cs","patch":"--- a/src/Foo.cs\n+++ b/src/Foo.cs\n@@ -1,3 +1,3 @@\n-old\n+new\n"}

```

        ## Dependencies

        Harbor.Abstractions only (uses StringBuilderPool from Harbor.Abstractions.Extensions).

        ## Permission rules

        Mutating. Context lines must match exactly. Atomic write (temp + rename). 5 000 000 char file cap, 5 000 line patch cap.

        ## See also

        - `docs/TOOLS_CATALOG.md` — full builtin tool catalogue.
        - `docs/ARCHITECTURE_LAYERS.md` — Clean Architecture layer rules (this project is
          Infrastructure; references Domain only).
        - `src/Harbor.Tools.Builtin/Harbor.Tools.Builtin.csproj` — facade that aggregates
          all 14 leaf tool projects.
