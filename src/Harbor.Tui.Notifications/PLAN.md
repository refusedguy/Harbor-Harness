# Plan — Harbor.Tui.Notifications

## Status: MVP

## Done

- [x] Implements ITuiRenderer contract
- [x] Streams assistant text deltas to console
- [x] Renders tool-call borders (start/end events)
- [x] Error rendering (red text)
- [x] Linux notify-send integration
- [x] macOS osascript integration
- [x] Graceful fallback when no notifier present

## TODO

- [ ] Inline image rendering (where supported)
- [ ] Token-usage footer
- [ ] Permission-prompt interactivity
- [ ] Windows BurntToast integration
- [ ] Action buttons (View Log, Retry)

## Known issues

- No Windows native toast support yet (planned).

## Next priorities

1. **P1**: Polish rendering for long tool outputs (truncation + expand)
2. **P2**: Theme/color customization via appsettings
