# Plan — Harbor.Tui.RazorConsole

## Status: Draft

## Done

- [x] Implements ITuiRenderer contract
- [x] Streams assistant text deltas to console
- [x] Renders tool-call borders (start/end events)
- [x] Error rendering (red text)
- [x] Basic .razor -> ANSI compilation
- [x] Sample ChatView

## TODO

- [ ] Inline image rendering (where supported)
- [ ] Token-usage footer
- [ ] Permission-prompt interactivity
- [ ] Full screen layout support
- [ ] Decide whether to keep or deprecate

## Known issues

- Stream-based model limits interactivity; may be removed.

## Next priorities

1. **P1**: Polish rendering for long tool outputs (truncation + expand)
2. **P2**: Theme/color customization via appsettings
