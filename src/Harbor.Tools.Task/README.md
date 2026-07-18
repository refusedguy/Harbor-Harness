        # Harbor.Tools.Task

        Part of the Harbor tool split — one of 14 leaf tool projects extracted out of the
        old `Harbor.Tools.Builtin` god-project. The `Harbor.Tools.Builtin` project remains
        as a thin facade that references all 14 leaves so existing consumers keep compiling
        without code changes.

        ## What it does

        `task` — Delegates work to a sub-agent. The sub-agent runs in its own context with limited permissions. Sequential execution mode.

        ## Args schema

        | Field | Type | Description |
|-------|------|-------------|
| `agent` | string | Required. Sub-agent name (e.g. 'explore'). |
| `prompt` | string | Required. Self-contained task description. |

        ## Example usage

        ```json
{"agent":"explore","prompt":"Find all TODO markers in src/"}
```

        ## Dependencies

        Harbor.Abstractions only (uses IAgentRegistry from Harbor.Abstractions.Agents — the registry implementation lives in Harbor.Core and is injected by the host).

        ## Permission rules

        Sequential. The target agent must be flagged IsSubAgent=true in its AgentDefinition. Validates agent name + existence before enqueuing.

        ## See also

        - `docs/TOOLS_CATALOG.md` — full builtin tool catalogue.
        - `docs/ARCHITECTURE_LAYERS.md` — Clean Architecture layer rules (this project is
          Infrastructure; references Domain only).
        - `src/Harbor.Tools.Builtin/Harbor.Tools.Builtin.csproj` — facade that aggregates
          all 14 leaf tool projects.
