# Harbor.Desktop.Abstractions

UI-framework-agnostic contracts shared by every Harbor desktop app
(Avalonia, WPF, MAUI, Blazor).

## What's shared

- **Base view-models** (`ViewModels/`): `ChatViewModelBase`,
  `DiffViewModelBase` and `CommandPaletteViewModelBase`. Each holds the
  observable state for its screen; a platform VM derives from one of them and
  adds platform-specific bindings. All three derive from
  <see cref="Harbor.Ui.Framework.ViewModels.StoreSubscriberViewModel" />,
  which provides store-subscription + selector-based projection.
  The shipped desktop app (`apps/Harbor.App.Avalonia`) derives its chat VM and
  its command palette from the two matching bases; `DiffViewModelBase` has no
  shipped subclass yet and is kept only because `Harbor.TestKit` builds a stub
  on it (#1835 tracks that).
  **#1835 removed the ten bases no shipped app derived from** —
  `SessionListViewModelBase`, `ProviderBrowserViewModelBase`,
  `SettingsViewModelBase`, `CodeEditorViewModelBase` / `EditorTabViewModelBase`,
  `TokenUsageViewModelBase`, `ToastNotificationViewModelBase`,
  `ThemeSettingsViewModelBase`, `FocusSessionViewModelBase` and
  `ProviderModelPickerViewModelBase`. They were public surface in a packable
  package with zero implementors; the concrete view-models that carry the same
  state (`DiffViewModel`, `FocusSessionViewModel`, `ProviderBrowserViewModel`,
  `ProviderModelPickerViewModel`, …) live here and do not derive from them.
  `tests/Harbor.Architecture.Tests/PublicAbstractBaseLivenessRules.cs` keeps
  the next one from being published.
- **Configuration** (`Configuration/`): `IAppConfigStore<T>` /
  `ICommonConfigStore` plus the shared config DTOs (`AppConfigBase`,
  `CommonConfig`, `CompositeConfig<T>`). Since #534 this leaf holds the
  **contracts and the schema only** — the JSON+filesystem stores
  (`JsonAppConfigStore<T>`, `JsonCommonConfigStore`) and the AOT
  `ConfigJsonContext` they need live in `Harbor.Hosting/Configuration`,
  because this project is `IsPackable` (`PackageId: Harbor.Desktop.Abstractions`)
  and the layer matrix calls it **Presentation** — which
  `PresentationCapabilityRules` bars from `System.IO.File*` in any case. See
  `tests/Harbor.Architecture.Tests/DesktopAbstractionsLeafTakesNoIoRules.cs`.
- **Messages** (`Messages/CrossVmMessages.cs`): typed cross-view-model events.
- **Service interfaces** (`Services/`): `IDispatcherAdapter`, `IThemeService`,
  `IFilePicker`, `IDialogService`, `IToastService`. Each platform implements
  these with its own native primitive.
- **Models** (`Models/`): `ThemeKind`, `ToastKind`, `ToastNotification`,
  `CommandPaletteItem`.
- **Design-system primitives** (`DesignSystem/`): `RgbColor`, `ColorPalette`
  (Catppuccin-Mocha + Latte constants), `DesignTokens` (spacing, radius,
  font sizes), `Typography` (font-family stacks).

## Dependency rules

✅ **Allowed**: `Harbor.Abstractions`, `Harbor.Application`
(documented exception #188, part of #96 — `ProviderPresets` catalog),
`Harbor.Terminal.Abstractions`, `Harbor.Ui.Framework` (+ its submodules:
`.Abstractions`, `.State`, `.Services`, `.Sessions`, `.ViewModels`),
`CommunityToolkit.Mvvm`, `Microsoft.Extensions.Logging.Abstractions`.

❌ **Forbidden**: any UI framework (`Avalonia*`, `System.Windows.*`,
`Microsoft.Maui.*`, `Microsoft.AspNetCore.Components.*`), any Infrastructure
project (`Harbor.Providers.*`, `Harbor.Storage.*`, `Harbor.Tools.*`),
`Harbor.Application` directly — the `Harbor.Core` facade was removed in #188 and
deleted in #451).

These rules are enforced by `tests/Harbor.Architecture.Tests`.

## Usage example

```csharp
// In apps/Harbor.App.Avalonia/ViewModels/ChatViewModel.cs
public sealed partial class ChatViewModel : ChatViewModelBase
{
    private readonly UiStore _store;
    private readonly TuiEffectHost _effects;
    private readonly IToastService _toasts;

    public ChatViewModel(
        UiStore store,
        TuiEffectHost effects,
        IDispatcherAdapter dispatcher,
        IToastService toasts,
        ILogger<ChatViewModel> logger)
        : base(dispatcher, logger)
    {
        _store = store;
        _effects = effects;
        _toasts = toasts;
    }

    [RelayCommand]
    private void Send() { /* forward to _effects */ }
}
```

See `apps/Harbor.App.Avalonia/ViewModels/ChatViewModel.cs` for the
fully-wired proof-of-concept.
