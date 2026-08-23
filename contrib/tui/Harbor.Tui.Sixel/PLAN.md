# Plan — Harbor.Tui.Sixel

## Status: MVP

## Done

- [x] Implements ITuiRenderer contract
- [x] Streams assistant text deltas to console
- [x] Renders tool-call borders (start/end events)
- [x] Error rendering (red text)
- [x] Sixel encoder for RGB images
- [x] Terminal capability detection via DA1
- [x] Graceful fallback to AnsiTuiRenderer

## TODO

- [ ] Inline image rendering (where supported)
- [ ] Token-usage footer
- [ ] Permission-prompt interactivity
- [ ] Animated GIF support
- [ ] Image scaling to terminal width

## Known issues

- Many terminals don't support Sixel — fallback is silent.

## Next priorities

1. **P1**: Polish rendering for long tool outputs (truncation + expand)
2. **P2**: Theme/color customization via appsettings
