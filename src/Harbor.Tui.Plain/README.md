# Harbor.Tui.Plain

Plain-text TUI renderer — no ANSI, no colors, no cursor movement. Designed for piped output, CI logs, screen readers, and accessibility. Output is a linear stream suitable for `>` redirection. Fallback renderer for minimal builds without Spectre (`TuiMode.ResolveTuiId`, `HARBOR_WITH_SPECTRE_TUI=false` ⇒ `plain`).

## Layer

Presentation — terminal renderer. References `Harbor.Terminal.Abstractions` (`ITuiRenderer`, `ITuiRenderContext`, `BaseTuiRenderer`).

## Dependencies

- `Harbor.Abstractions` (Domain)
- `Harbor.Terminal.Abstractions`

## Public API

- `PlainTuiRenderer : BaseTuiRenderer` — implements `ITuiRenderer` (`PlainTuiRenderer.cs`)

## Usage

Set `HARBOR_TUI=plain` or pipe stdout to a file (`src/Harbor.Hosting`/`apps/Harbor.App.Cli/Hosting/TuiMode.cs`). Output shapes seen today:

- `[assistant] ` prefix at message start; deltas streamed verbatim (`PlainTuiRenderer.cs:41-47`)
- Live tool line per call: `→ {tool} {args}` (`PlainTuiRenderer.cs:54-58`)
- Compaction summary: `[compacted: pruned N msgs, saved ~X tokens]` (`PlainTuiRenderer.cs:60-61`)
- Errors: `[ERROR] {message}` (`PlainTuiRenderer.cs:63-64`)

This exact surface is what the E2E smoke tests assert against (see AGENTS.md E2E section).

## See also

- [../../docs/ARCHITECTURE_LAYERS.md](../../docs/ARCHITECTURE_LAYERS.md)
- [../../docs/ALTERNATIVE_UIS.md](../../docs/ALTERNATIVE_UIS.md) — full TUI/GUI renderer comparison
