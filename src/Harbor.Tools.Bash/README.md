        # Harbor.Tools.Bash

        Part of the Harbor tool split — one of 14 leaf tool projects extracted out of the
        old `Harbor.Tools.Builtin` god-project. The `Harbor.Tools.Builtin` project remains
        as a thin facade that references all 14 leaves so existing consumers keep compiling
        without code changes.

        ## What it does

        `bash` — Executes shell commands. Captures stdout/stderr/exit code. Commands run in the current working directory; pass `cwd` to override.

        ## Args schema

        | Field | Type | Description |

|-------|------|-------------|
| `command` | string | Required. Shell command line. |
| `cwd` | string | Optional. Working directory override. |
| `timeout` | integer | Optional. Max seconds. |

        ## Example usage

        ```json

{"command":"dotnet build","cwd":"/repo","timeout":120}

```

        ## Dependencies

        Harbor.Abstractions only (uses StringBuilderPool from Harbor.Abstractions.Extensions).

        ## Permission rules

        Sequential. Subject to PermissionRuleset — typically gated to a sandbox cwd. Use sb-kill / sb-timeout via host.

        ## See also

        - `docs/TOOLS_CATALOG.md` — full builtin tool catalogue.
        - `docs/ARCHITECTURE_LAYERS.md` — Clean Architecture layer rules (this project is
          Infrastructure; references Domain only).
        - `src/Harbor.Tools.Builtin/Harbor.Tools.Builtin.csproj` — facade that aggregates
          all 14 leaf tool projects.
