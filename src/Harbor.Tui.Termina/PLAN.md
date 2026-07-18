# Plan — Harbor.Tui.Termina

## Status: Draft

## Done

- [x] Implements ITuiRenderer contract
- [x] Streams assistant text deltas to console
- [x] Renders tool-call borders (start/end events)
- [x] Error rendering (red text)
- [x] Basic chat view
- [x] Source generator integration

## TODO

- [ ] Inline image rendering (where supported)
- [ ] Token-usage footer
- [ ] Permission-prompt interactivity
- [ ] Full interactive REPL
- [ ] Panel system support

## Known issues

- Generator + .NET 10 SDK has known conflicts — analyzer is removed before CoreCompile.

## Next priorities

1. **P1**: Polish rendering for long tool outputs (truncation + expand)
2. **P2**: Theme/color customization via appsettings
