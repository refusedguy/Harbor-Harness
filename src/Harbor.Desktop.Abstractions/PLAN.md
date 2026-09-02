# Plan - Harbor.Desktop.Abstractions

## Status: MVP

Shared view-model base classes + service interfaces + config stores for Harbor desktop apps. Created in S3 to eliminate VM duplication across desktop UIs; Avalonia is the only platform shipped today.

## Done

- [x] `ViewModelBase` → deprecated and removed; all VMs now derive from `StoreSubscriberViewModel` (Harbor.Ui.Framework.ViewModels) — verified on `ChatViewModelBase.cs:31`, `SessionListViewModelBase.cs:12`
- [x] 13 base VMs: Chat, SessionList, ProviderBrowser, Settings, CodeEditor, Diff, TokenUsage, CommandPalette, ToastNotification, ThemeSettings, FocusSession, ProviderModelPicker (+ concrete platform-facing VMs)
- [x] Service interfaces: `IDispatcherAdapter`, `IThemeService`, `IFilePicker`, `IDialogService`, `IToastService`
- [x] Configuration stores shared by all desktop apps (`Configuration/JsonAppConfigStore`, `JsonCommonConfigStore`)
- [x] Terminal.Abstractions integration (panel contracts referenced without pulling a renderer)

## TODO

- [ ] Document the VM state machine for each base VM (transition diagrams)
- [ ] Add unit tests for VM state transitions
- [ ] Add `IMultiWindowService` for detachable panels
- [ ] Platform implementation audit (verify each platform correctly implements the service interfaces)
- [ ] Theme service: add HighContrast accessibility theme

## Known issues

- Each desktop app still has some platform-specific VM duplication beyond the base classes.
- IDispatcherAdapter implementations vary in correctness (Avalonia verified; WPF / MAUI / Blazor apps do not exist in-repo — only `apps/Harbor.App.Avalonia` and `apps/Harbor.App.Cli` are present).
- No multi-window support yet (panels are docked only).

## Next priorities

1. **P0**: Audit + verify each platform's service-interface implementations
2. **P1**: Add VM state-transition unit tests
3. **P1**: HighContrast accessibility theme
4. **P2**: Multi-window support (detachable panels)
5. **P2**: Theme picker UI in Settings VM

## See also

- [README.md](README.md)
- [../../docs/DESKTOP_APP_PLAN.md](../../docs/DESKTOP_APP_PLAN.md)
- [../../docs/ALTERNATIVE_UIS.md](../../docs/ALTERNATIVE_UIS.md)
