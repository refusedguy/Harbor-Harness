# Plan — Harbor.Tui.Abstractions

## Status: Stable

## Done

- [x] Implements ITuiRenderer contract
- [x] Streams assistant text deltas to console
- [x] Renders tool-call borders (start/end events)
- [x] Error rendering (red text)
- [x] UiStore observable
- [x] UiReducer state machine
- [x] Panel-system contracts (ITuiPanelPlugin)
- [x] Full XML docs on every public API

## TODO

- [ ] Inline image rendering (where supported)
- [ ] Token-usage footer
- [ ] Permission-prompt interactivity
- [ ] Formalize panel lifecycle (mount/unmount events)
- [ ] Add ITuiLayoutProvider for pluggable layouts

## Known issues

- No formal panel lifecycle event yet — panels use Initialize/Shutdown from IPlugin.

## Next priorities

1. **P1**: Polish rendering for long tool outputs (truncation + expand)
2. **P2**: Theme/color customization via appsettings
