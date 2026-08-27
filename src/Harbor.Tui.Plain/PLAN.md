# Plan — Harbor.Tui.Plain

## Status: Stable

## Done

- [x] Implements `ITuiRenderer` (`PlainTuiRenderer : BaseTuiRenderer`)
- [x] Streams assistant text deltas to console
- [x] Live tool-call lines (`→ tool args`)
- [x] Error lines (`[ERROR] …`) and compaction markers
- [x] Zero ANSI escapes — linear streaming output
- [x] Baseline renderer for minimal builds + CI/E2E assertions

## TODO

- [ ] Optional timestamp prefix
- [ ] JSON Lines output mode (for structured logging)
- [ ] Permission-prompt interactivity (non-interactive by design today)

## Known issues

- No interactivity — read-only.
