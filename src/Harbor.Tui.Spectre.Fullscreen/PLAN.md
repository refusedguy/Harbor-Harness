# Plan — Harbor.Tui.Spectre.Fullscreen

## Status: MVP

## Done

- [x] Implements ITuiRenderer contract
- [x] Streams assistant text deltas to console
- [x] Renders tool-call borders (start/end events)
- [x] Error rendering (red text)
- [x] Three-panel live layout
- [x] Streaming chat panel
- [x] Tool activity panel

## TODO

- [ ] Inline image rendering (where supported)
- [ ] Token-usage footer
- [ ] Permission-prompt interactivity
- [ ] Inline input box (currently read-line at bottom)
- [ ] Resize handling

## Known issues

- No inline input — uses Console.ReadLine at bottom.

## Next priorities

1. **P1**: Polish rendering for long tool outputs (truncation + expand)
2. **P2**: Theme/color customization via appsettings
