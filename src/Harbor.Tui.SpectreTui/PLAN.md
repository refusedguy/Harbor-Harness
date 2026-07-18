# Plan — Harbor.Tui.SpectreTui

## Status: Stable

## Done

- [x] Implements ITuiRenderer contract
- [x] Streams assistant text deltas to console
- [x] Renders tool-call borders (start/end events)
- [x] Error rendering (red text)
- [x] Interactive input box with history
- [x] Scrollable transcript
- [x] Command palette (Cmd+K)
- [x] Plugin panel host
- [x] Token-usage footer

## TODO

- [ ] Inline image rendering (where supported)
- [ ] Token-usage footer
- [ ] Permission-prompt interactivity
- [ ] Mouse support (click to scroll, drag dividers)
- [ ] Configurable keybindings
- [ ] Theme picker

## Known issues

- Mouse support not wired yet (planned).

## Next priorities

1. **P1**: Polish rendering for long tool outputs (truncation + expand)
2. **P2**: Theme/color customization via appsettings
