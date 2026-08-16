# Plan - Harbor.Desktop.Abstractions

## Status: MVP

Shared view-model base classes + service interfaces for all Harbor desktop apps (Avalonia, WPF, MAUI, Blazor). Created by S3 to eliminate VM duplication across the 4 desktop apps.

## Done

- [x] `ViewModelBase` → deprecated and removed; all VMs now derive from `StoreSubscriberViewModel` (Harbor.Ui.Framework.ViewModels)
- [x] 10 base VMs: Chat, SessionList, ProviderBrowser, Settings, CodeEditor, Diff, TokenUsage, CommandPalette, ToastNotification, ...
- [x] Service interfaces: `IDispatcherAdapter`, `IThemeService`, `IFilePicker`, `IDialogService`, `IToastService`
- [x] Harbor.Tui.Abstractions integration (UiStore observable -> VM updates)
- [x] InternalsVisibleTo: Harbor.App.Avalonia, Harbor.App.Wpf, Harbor.App.Maui, Harbor.App.Blazor

## TODO

- [ ] Document the VM state machine for each base VM (transition diagrams)
- [ ] Add unit tests for VM state transitions
- [ ] Add `IMultiWindowService` for detachable panels
- [ ] Platform implementation audit (verify each platform correctly implements the service interfaces)
- [ ] Theme service: add HighContrast accessibility theme

## Known issues

- Each desktop app still has some platform-specific VM duplication beyond the base classes.
- IDispatcherAdapter implementations vary in correctness (Avalonia verified, WPF verified, MAUI untested, Blazor untested).
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
