# Plan — Harbor.App.Wpf

## Status: MVP

Windows-native WPF desktop GUI for Harbor. Targets `net10.0-windows10.0.19041`. Not cross-platform — for cross-platform desktop use `Harbor.App.Avalonia`.

## Done

- [x] WPF + CommunityToolkit.Mvvm + AvalonEdit + LiveCharts + AvalonDock integration
- [x] Microsoft.Extensions.Hosting bootstrap
- [x] Catppuccin Mocha / Latte themes
- [x] PerMonitorV2 DPI awareness (app.manifest)
- [x] Sidebar + chat + code editor (AvalonEdit) + dock manager (AvalonDock)
- [x] Token-usage chart (LiveCharts)
- [x] Markdown rendering (Markdig -> WPF FlowDocument)
- [x] Command palette
- [x] Settings UI
- [x] Multi-provider + multi-model switching

## TODO

- [ ] Auto-update (Velopack or Squirrel.Windows)
- [ ] MSIX packaging for Microsoft Store
- [ ] Windows code signing
- [ ] System tray icon + background runs
- [ ] Windows native notifications (toast)
- [ ] Touch / pen input for Surface devices
- [ ] High-contrast accessibility theme

## Known issues

- Warnings relaxed (`TreatWarningsAsErrors=false`) — WPF codegen fights with repo-wide analyzers.
- Many CA warnings suppressed (CA1416, CA1859, etc.) — WPF idiom vs analyzer rules.
- No MSIX packaging yet — currently ships as a raw `.exe`.

## Next priorities

1. **P0**: MSIX packaging + Microsoft Store listing
2. **P1**: Auto-update (Velopack)
3. **P1**: Windows code signing
4. **P2**: System tray + toast notifications
5. **P2**: High-contrast accessibility theme

## See also

- [README.md](README.md) — full app README
- [../../docs/ALTERNATIVE_UIS.md](../../docs/ALTERNATIVE_UIS.md)
- [../../docs/DESKTOP_APP_PLAN.md](../../docs/DESKTOP_APP_PLAN.md)
- [../Harbor.App.Avalonia/README.md](../Harbor.App.Avalonia/README.md) — cross-platform desktop alternative
