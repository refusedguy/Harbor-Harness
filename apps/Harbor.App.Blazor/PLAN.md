# Plan — Harbor.App.Blazor

## Status: MVP

## Done

- [x] Kestrel + Blazor Server bootstrap
- [x] Chat transcript, input box, tool-call cards, status bar
- [x] UiStore integration (same state model as TUI)
- [x] Streaming assistant deltas via SignalR
- [x] Markdown rendering (Markdig)
- [x] Multi-session tabs (basic)

## TODO

- [ ] Auth + HTTPS (currently localhost-only)
- [ ] File upload (drag-drop into chat)
- [ ] Diff view for `write` / `edit` tool calls
- [ ] Mobile-optimized layout (responsive)
- [ ] PWA support (offline-capable shell)
- [ ] Multi-user collaboration (shared session)
- [ ] Code editor (Monaco wrapper)

## Known issues

- No auth — never expose on `0.0.0.0` untrusted.
- Long transcripts cause Blazor re-render lag (10k+ messages).
- File upload not supported yet.

## Next priorities

1. **P0**: Auth + HTTPS for remote deployment
2. **P1**: File upload (drag-drop)
3. **P1**: Diff view for tool calls
4. **P2**: PWA shell
5. **P2**: Monaco code editor
