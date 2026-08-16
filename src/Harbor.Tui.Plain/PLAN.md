# Plan — Harbor.Tui.Plain

## Status: Stable

## Done

- [x] Implements ITuiRenderer contract
- [x] Streams assistant text deltas to console
- [x] Renders tool-call borders (start/end events)
- [x] Error rendering (red text)
- [x] Zero ANSI escapes
- [x] Linear streaming output
- [x] Bracketed tool-call markers

## TODO

- [ ] Inline image rendering (where supported)
- [ ] Token-usage footer
- [ ] Permission-prompt interactivity
- [ ] Optional timestamp prefix
- [ ] JSON Lines output mode (for structured logging)

## Known issues

- No interactivity — read-only.

## Next priorities

1. **P1**: Polish rendering for long tool outputs (truncation + expand)
2. **P2**: Theme/color customization via appsettings
