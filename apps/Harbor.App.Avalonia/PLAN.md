# Plan — Harbor.App.Avalonia

## Status: MVP

The Avalonia app is the strategic cross-platform desktop GUI for Harbor (Windows, Linux, macOS — all from one codebase).

## Done

- [x] Avalonia 11.2 + AvaloniaEdit + CommunityToolkit.Mvvm + Markdig integration
- [x] Microsoft.Extensions.Hosting bootstrap
- [x] Sidebar + chat + code editor + status bar layout (1280x800)
- [x] Catppuccin-Mocha dark theme
- [x] Streaming chat with markdown rendering
- [x] Session manager (list / load / save)
- [x] Command palette (Cmd+K)
- [x] Diff view for `write` / `edit` tool calls
- [x] Token-usage chart
- [x] Plugin panel host
- [x] Multi-provider + multi-model switching
- [x] Settings UI (provider, model, theme, permissions)

## TODO

- [ ] Mobile / touch layout (currently desktop-only)
- [ ] Native file dialogs (currently uses Avalonia's built-in)
- [ ] System tray icon + background runs
- [ ] Native notifications on completion (per-OS)
- [ ] Auto-update (via Velopack or similar)
- [ ] Multi-window (detachable panels)
- [ ] Theme picker (Mocha / Latte / custom)
- [ ] Mac notarization + Windows code signing
- [ ] Linux AppImage / Flatpak packaging

## Known issues

- Avalonia 11.2.7 brings `Tmds.DBus.Protocol 0.20.0` transitively (GHSA-xrw6-gwf8-vvr9) — suppressed NU1903, no fix available until Avalonia 11.3.
- AVLN1000/AVLN1001 warnings from XAML codegen suppressed.
- MA0046 (EventHandler<T> with record payloads) suppressed — intentional for simple event contracts.

## Next priorities

1. **P0**: Auto-update (Velopack integration)
2. **P1**: Mac notarization + Windows code signing
3. **P1**: System tray + background runs
4. **P2**: Linux AppImage / Flatpak
5. **P2**: Multi-window detachable panels

## See also

- [README.md](README.md) — full app README
- [../../docs/ALTERNATIVE_UIS.md](../../docs/ALTERNATIVE_UIS.md)
- [../../docs/DESKTOP_APP_PLAN.md](../../docs/DESKTOP_APP_PLAN.md)
