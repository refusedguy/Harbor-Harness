# Harbor.Desktop.Abstractions

UI-framework-agnostic contracts shared by every Harbor desktop app
(Avalonia, WPF, MAUI, Blazor).

## What's shared

- **Base view-models** (`ViewModels/`): `ChatViewModelBase`,
  `SessionListViewModelBase`, `ProviderBrowserViewModelBase`,
  `SettingsViewModelBase`, `CodeEditorViewModelBase`, `DiffViewModelBase`,
  `TokenUsageViewModelBase`, `CommandPaletteViewModelBase`,
  `ToastNotificationViewModelBase`. Each holds the observable state for its
  screen; platform VMs derive from these and add platform-specific bindings.
  All derive from <see cref="Harbor.Ui.Framework.ViewModels.StoreSubscriberViewModel" />,
  which provides store-subscription + selector-based projection.
- **Service interfaces** (`Services/`): `IDispatcherAdapter`, `IThemeService`,
  `IFilePicker`, `IDialogService`, `IToastService`. Each platform implements
  these with its own native primitive.
- **Models** (`Models/`): `ThemeKind`, `ToastKind`, `ToastNotification`,
  `CommandPaletteItem`.
- **Design-system primitives** (`DesignSystem/`): `RgbColor`, `ColorPalette`
  (Catppuccin-Mocha + Latte constants), `DesignTokens` (spacing, radius,
  font sizes), `Typography` (font-family stacks).

## Dependency rules

✅ **Allowed**: `Harbor.Abstractions`, `Harbor.Core`, `Harbor.Tui.Abstractions`,
`CommunityToolkit.Mvvm`, `Microsoft.Extensions.Logging.Abstractions`.

❌ **Forbidden**: any UI framework (`Avalonia*`, `System.Windows.*`,
`Microsoft.Maui.*`, `Microsoft.AspNetCore.Components.*`), any Infrastructure
project (`Harbor.Providers.*`, `Harbor.Storage.*`, `Harbor.Tools.*`).

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
