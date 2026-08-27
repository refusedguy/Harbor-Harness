# Plan — Harbor.Tui.Ansi

## Status: Stable

## Done

- [x] Implements `ITuiRenderer` (`AnsiTuiRenderer : BaseTuiRenderer`)
- [x] Streams assistant text deltas to console
- [x] Renders tool-execution borders
- [x] Error rendering
- [x] NO_COLOR / TERM=dumb handling
- [x] UTF-8 output enforcement
- [x] `TerminalQrRenderer` QR-block output (daemon pairing)

## TODO

- [ ] Inline image rendering (where supported)
- [ ] Token-usage footer
- [ ] Permission-prompt interactivity
- [ ] Inline progress spinners

## Known issues

- On Windows cmd.exe (legacy conhost), some escapes are not interpreted — use Windows Terminal.
