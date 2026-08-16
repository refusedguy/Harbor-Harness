# Plan — Harbor.Tui.Ansi

## Status: Stable

## Done

- [x] Implements ITuiRenderer contract
- [x] Streams assistant text deltas to console
- [x] Renders tool-call borders (start/end events)
- [x] Error rendering (red text)
- [x] NO_COLOR / TERM=dumb detection
- [x] UTF-8 output enforcement
- [x] Cursor-save/restore for inline tool updates

## TODO

- [ ] Inline image rendering (where supported)
- [ ] Token-usage footer
- [ ] Permission-prompt interactivity
- [ ] 256-color / truecolor support
- [ ] Inline progress spinners

## Known issues

- On Windows cmd.exe (legacy conhost), some escapes are not interpreted — use Windows Terminal.

## Next priorities

1. **P1**: Polish rendering for long tool outputs (truncation + expand)
2. **P2**: Theme/color customization via appsettings
