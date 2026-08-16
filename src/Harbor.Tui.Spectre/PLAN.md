# Plan — Harbor.Tui.Spectre

## Status: Stable

## Done

- [x] Implements ITuiRenderer contract
- [x] Streams assistant text deltas to console
- [x] Renders tool-call borders (start/end events)
- [x] Error rendering (red text)
- [x] Panel rendering for assistant messages
- [x] Markdown -> Spectre markup conversion
- [x] Color + emoji

## TODO

- [ ] Inline image rendering (where supported)
- [ ] Token-usage footer
- [ ] Permission-prompt interactivity
- [ ] Live table updates (StreamingLayout)

## Known issues

- Not interactive — use SpectreTui for REPL.

## Next priorities

1. **P1**: Polish rendering for long tool outputs (truncation + expand)
2. **P2**: Theme/color customization via appsettings
