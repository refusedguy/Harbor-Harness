# Plan - Harbor.Desktop.Abstractions

## Status: MVP

The shared configuration schema, the config-store ports, the cross-view-model
messages and the shared models/enums for the Harbor desktop apps. The
concrete desktop view-models live in `Harbor.Desktop.ViewModels` since #1899.

## Done

- [x] `ViewModelBase` → deprecated and removed; all VMs now derive from `StoreSubscriberViewModel` (Harbor.Ui.Framework.ViewModels)
- [x] 13 base VMs: Chat, SessionList, ProviderBrowser, Settings, CodeEditor, Diff, TokenUsage, CommandPalette, ToastNotification, ThemeSettings, FocusSession, ProviderModelPicker (+ concrete platform-facing VMs) — **ten of them removed in #1835**: they had no implementor in the tree, so they were published public surface with nothing behind them. The three that stayed (`ChatViewModelBase`, `DiffViewModelBase`, `CommandPaletteViewModelBase`) moved to `Harbor.Desktop.ViewModels` with the rest of the view-models in #1899.
- [x] Service interfaces (`IDispatcherAdapter`, `IThemeService`, `IFilePicker`, `IDialogService`, `IToastService`) — **gone from this project**: they lived in the `Services/` folder that #1899's predecessor (#534's move of the config stores) had already emptied, and the platform implementations live with the apps.
- [x] Configuration stores shared by all desktop apps — **ports** (`Configuration/IAppConfigStore<T>`, `ICommonConfigStore`) stay here; the JSON+filesystem implementations moved to `Harbor.Hosting/Configuration` in #534
- [x] #1899: the assembly became a LEAF — one Harbor reference (`Harbor.Abstractions.Contracts`), a two-assembly closure, and the `#188` Presentation → Application edge moved to `Harbor.Desktop.ViewModels` with the view-models that read the preset catalogue.

## TODO

- [ ] Delete `ThemeKind` and `ToastKind` — both are referenced by nothing in the tree, and `ToastKind` duplicates the one in `Harbor.Ui.Framework.Services`. Published surface, so it is an owner decision, not a cleanup.
- [ ] Move the config STORES' `Result` vocabulary to `Harbor.Abstractions.Contracts` so the leaf does not need `CSharpFunctionalExtensions` directly.

## Known issues

- The assembly is `IsPackable` (`PackageId Harbor.Desktop.Abstractions`) and nothing has ever been published to a feed, so the package identity is a forward-looking commitment rather than a live contract. The versioning decision for it belongs to the owner (ADR-020).

## See also

- [README.md](README.md)
- [ADR-020](../../docs/adr/ADR-020-desktop-abstractions-split.md)
- [../../docs/CONFIGURATION.md](../../docs/CONFIGURATION.md)
- [../../docs/ARCHITECTURE_LAYERS.md](../../docs/ARCHITECTURE_LAYERS.md)
