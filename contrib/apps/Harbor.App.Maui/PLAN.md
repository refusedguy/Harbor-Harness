# Plan — Harbor.App.Maui

## Status: Draft

Skeleton project — boots, shows a basic chat view. Not feature-complete.

## Done

- [x] MAUI project skeleton (Windows + macOS Catalyst targets)
- [x] CPM-compatible package references (SkipValidateMauiImplicitPackageReferences)
- [x] Basic chat view (transcript + input)
- [x] UiStore integration (same state model as TUI)

## TODO

- [ ] Code editor (MonkeyPatch / AvaloniaEdit port — MAUI has no native code editor)
- [ ] Session manager UI
- [ ] Command palette
- [ ] Diff view
- [ ] Token-usage chart
- [ ] Plugin panel host
- [ ] iOS + Android targets (currently Win + macOS only)
- [ ] Native push notifications (background agent completion)
- [ ] Mac Catalyst entitlements + notarization

## Known issues

- No code editor — only chat view.
- iOS/Android not targeted yet.
- Build fails on Linux (MAUI workload not available — by design).

## Next priorities

1. **P1**: Decide whether to keep MAUI app or focus on Avalonia (cross-platform via Avalonia is the strategic recommendation in `docs/ALTERNATIVE_UIS.md`)
2. **P1**: If keeping MAUI, port the code editor from AvaloniaEdit
3. **P2**: iOS + Android targets
4. **P2**: Native push notifications

## Recommendation

The Avalonia app (`apps/Harbor.App.Avalonia/`) is the strategic cross-platform desktop GUI. MAUI is kept as a research project for native iOS/Android support, but is not the primary desktop target.
