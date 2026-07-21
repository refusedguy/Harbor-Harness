        # Harbor.Tools.Edit

        Part of the Harbor tool split — one of 14 leaf tool projects extracted out of the
        old `Harbor.Tools.Builtin` god-project. The `Harbor.Tools.Builtin` project remains
        as a thin facade that references all 14 leaves so existing consumers keep compiling
        without code changes.

        ## What it does

        `edit` — Surgical string replace. oldString must be unique unless replaceAll. Multi-edit applies in order on the updated buffer.

        ## Args schema

        | Field | Type | Description |

|-------|------|-------------|
| `path` | string | Required. Target file path. |
| `oldString` | string | Required. Text to find. |
| `newString` | string | Required. Replacement text. |
| `replaceAll` | bool | Optional. Default false. |

        ## Example usage

        ```json

{"path":"Config.cs","oldString":"v1","newString":"v2"}

```

        ## Dependencies

        Harbor.Abstractions only.

        ## Permission rules

        Mutating. oldString must be unique unless replaceAll. 5 000 000 char file cap.

        ## See also

        - `docs/TOOLS_CATALOG.md` — full builtin tool catalogue.
        - `docs/ARCHITECTURE_LAYERS.md` — Clean Architecture layer rules (this project is
          Infrastructure; references Domain only).
        - `src/Harbor.Tools.Builtin/Harbor.Tools.Builtin.csproj` — facade that aggregates
          all 14 leaf tool projects.
