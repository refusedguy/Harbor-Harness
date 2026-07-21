        # Harbor.Tools.WebFetch

        Part of the Harbor tool split — one of 14 leaf tool projects extracted out of the
        old `Harbor.Tools.Builtin` god-project. The `Harbor.Tools.Builtin` project remains
        as a thin facade that references all 14 leaves so existing consumers keep compiling
        without code changes.

        ## What it does

        `webfetch` — Fetches a URL and returns its content as markdown (HTML stripped, code kept, links inlined). Uses a shared HttpClient; respects redirects and sets a realistic User-Agent.

        ## Args schema

        | Field | Type | Description |

|-------|------|-------------|
| `url` | string | Required. HTTP(S) URL. |
| `maxChars` | integer | Optional. Default 50 000, hard cap 500 000. |

        ## Example usage

        ```json

{"url":"https://example.com/docs","maxChars":20000}

```

        ## Dependencies

        Harbor.Abstractions only (uses StringBuilderPool from Harbor.Abstractions.Extensions).

        ## Permission rules

        Read-only (network). 5 MiB hard download cap. Returns markdown; raw HTML never reaches the model context.

        ## See also

        - `docs/TOOLS_CATALOG.md` — full builtin tool catalogue.
        - `docs/ARCHITECTURE_LAYERS.md` — Clean Architecture layer rules (this project is
          Infrastructure; references Domain only).
        - `src/Harbor.Tools.Builtin/Harbor.Tools.Builtin.csproj` — facade that aggregates
          all 14 leaf tool projects.
