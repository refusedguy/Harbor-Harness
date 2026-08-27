# Plan — Harbor.Tui.Notifications

## Status: MVP

## Done

- [x] Implements `ITuiRenderer` (`NotificationTuiRenderer : BaseTuiRenderer`)
- [x] Desktop OS notifications on agent events: errors, completion, compaction, tool failures
- [x] Linux notify-send integration (`LinuxNotifySendBackend`)
- [x] macOS osascript integration (`MacOsascriptBackend`)
- [x] Windows msg.exe backend + null fallback when no notifier present (`WindowsToastBackend`, `NullNotificationBackend`)

## TODO

- [ ] Native Windows Action Center toasts (WinRT `ToastNotificationManager` / snoretoast)
- [ ] Notification deduplication — debounce window per category
- [ ] Click-through action buttons (View Log, Retry)

## Known issues

- No Windows native toast support yet (`msg.exe` modal dialog is used as a stopgap).
