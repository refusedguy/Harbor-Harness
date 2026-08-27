# Harbor.Tui.Ansi

ANSI streaming TUI renderer using raw escape codes. AOT-compatible. Writes assistant text deltas, tool-call borders, and errors inline to the console.

## Layer

Presentation — terminal renderer. References `Harbor.Terminal.Abstractions` (`ITuiRenderer`, `ITuiRenderContext`, `BaseTuiRenderer`).

## Dependencies

- `Harbor.Abstractions` (Domain)
- `Harbor.Terminal.Abstractions`
- `Microsoft.Extensions.Logging.Abstractions`

## Public API

- `AnsiTuiRenderer : BaseTuiRenderer` — implements `ITuiRenderer`; intentionally unsealed for subclassing (`AnsiTuiRenderer.cs:26`)
- `AnsiRenderContext : ITuiRenderContext` — writes through the shared render pipeline (`AnsiTuiRenderer.cs:166`)
- `Ansi` — static ANSI escape-sequence constants (`Ansi.cs`)
- `TerminalQrRenderer` — renders QR codes as ANSI blocks (used by daemon pairing output)

## Usage

Selected with `HARBOR_TUI=ansi`; registered by the host's renderer resolution (`TuiMode`). Events arrive from the agent via the event bus; each is folded into the render context by `RenderAsync`.

## Terminal detection

Detects `NO_COLOR` / `TERM=dumb` conditions; on legacy conhost some escapes degrade gracefully — use Windows Terminal for full fidelity.

## See also

- [../../docs/ARCHITECTURE_LAYERS.md](../../docs/ARCHITECTURE_LAYERS.md)
- [../../docs/ALTERNATIVE_UIS.md](../../docs/ALTERNATIVE_UIS.md) — full TUI/GUI renderer comparison
