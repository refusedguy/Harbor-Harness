# Plan — Harbor.Tui.TerminalGui

## Status: Draft

## Done

- [x] Implements ITuiRenderer contract
- [x] Streams assistant text deltas to console
- [x] Renders tool-call borders (start/end events)
- [x] Error rendering (red text)
- [x] Basic chat view with TextView
- [x] Application.Init integration

## TODO

- [ ] Inline image rendering (where supported)
- [ ] Token-usage footer
- [ ] Permission-prompt interactivity
- [ ] Dialog for permission prompts
- [ ] Menu bar with command shortcuts
- [ ] Clean shutdown (Application.Shutdown race)

## Known issues

- Application.Init/Shutdown lifecycle is tricky under DI; can crash on exit.

## Next priorities

1. **P1**: Polish rendering for long tool outputs (truncation + expand)
2. **P2**: Theme/color customization via appsettings
