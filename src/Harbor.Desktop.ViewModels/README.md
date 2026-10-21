# Harbor.Desktop.ViewModels

The concrete desktop UI layer: the view-models every Harbor desktop app
(Avalonia, WPF, MAUI, Blazor) renders. It is a **Presentation** assembly
(`docs/ARCHITECTURE_LAYERS.md` §1) and it is the reason
`Harbor.Desktop.Abstractions` can be a leaf.

## What it is

Until #1899 this assembly was `Harbor.Desktop.Abstractions/ViewModels/` — the
`ViewModels/` folder of an assembly whose name promised a contract layer and
which held 46 public types, 9 project references and an 18-assembly dependency
closure. It was a hub wearing a leaf's name.

In #1899 the assembly was split at the measured dependency seam:

| assembly | holds | Harbor references | closure |
|---|---|---|---|
| `Harbor.Desktop.Abstractions` | the config schema, the config-store ports, the cross-view-model messages, the shared models/enums | 1 (`Harbor.Abstractions.Contracts`) | 2 |
| `Harbor.Desktop.ViewModels` | this: the concrete view-models and their abstract bases | 8 | 15 |

The decision, the measured cost of each option and the open questions are in
[ADR-021](../../docs/adr/ADR-021-desktop-abstractions-split.md).

## Public API

The abstract bases a platform view-model derives from, and the concrete
view-models the shipped app uses. All in namespace `Harbor.Desktop.ViewModels`.

Bases (all derive from `Harbor.Ui.Framework.ViewModels.StoreSubscriberViewModel`):

- `ChatViewModelBase` — the streaming chat screen: `InputText`, `IsAgentRunning`,
  `Lines`, `ToolCalls`, `Timeline`, `RoleBrushKey(ChatRole)`.
  Derived by `apps/Harbor.App.Avalonia/ViewModels/ChatViewModel.cs`.
- `CommandPaletteViewModelBase` — the command palette: `Results`, `InvokeSelected()`,
  `MoveUp()`, `MoveDown()`. Derived by the Avalonia app's `CommandPaletteViewModel`.
- `DiffViewModelBase` — the diff screen. Its only implementor in the tree is the
  `Harbor.TestKit` stub; the Avalonia shell renders diffs through the concrete
  `DiffViewModel` (tracked by #1875).

Concrete view-models: `DiffViewModel`, `OnboardingViewModel`,
`ProviderConfigViewModel`, `ProviderModelPickerViewModel`,
`ProviderBrowserViewModel`, `FocusSessionViewModel`, `EditorTabViewModel`,
`MonacoEditorViewModel`, `SessionCardViewModel`, `ModelRateLabel`,
`CommandResultViewModel`, `PickerModelViewModel`, `OnboardingProviderOption`,
`ProviderModelLoadMessages`, and the shared row models in `SharedDataModels.cs`
(`ChatMessageViewModel`, `SessionEntryViewModel`, `ProviderEntryViewModel`,
`ModelEntryViewModel`, `CommandEntry`, `TokenBarViewModel`, `ToastViewModel`).

## Wiring

No activation of its own: these are plain classes, and the composition root
decides which ones exist. The Avalonia app registers its platform view-models in
`apps/Harbor.App.Avalonia/Hosting/ViewModelRegistration.cs`; the onboarding and
provider-picker view-models are constructed by
`apps/Harbor.App.Avalonia/Hosting/ConfigRegistration.cs`.

## Usage

```csharp
using Harbor.Desktop.ViewModels;

// A platform view-model derives from the base and adds the platform wiring.
public sealed partial class ChatViewModel : ChatViewModelBase
{
    protected override void OnStoreChanged(UiState state)
    {
        // Project the TEA state onto the observable properties above.
    }
}
```

## Dependencies

- `Harbor.Abstractions` — session and provider models.
- `Harbor.Desktop.Abstractions` — the config schema (`CommonConfig`,
  `ICommonConfigStore`), the shared models (`SessionDotState`) and the
  cross-view-model messages.
- `Harbor.Ui.Framework.ViewModels` / `.State` / `.Services` / `.Sessions` /
  `.Rendering` — the TEA state, the dispatcher/theme services, the session
  factory and the diff engine.
- `Harbor.PolicyConfig` — the `ProviderPresets` catalogue, a documented
  Presentation → Application exception (#188/#96) that the two onboarding
  view-models read. It is scoped to those two files. The catalogue moved
  out of `Harbor.Application` into `Harbor.PolicyConfig` in #1625 and the
  exception followed it there; the namespace it is declared in is still
  `Harbor.Application.Configuration`.
- Referenced by: `apps/Harbor.App.Avalonia`, `tests/Harbor.TestKit`,
  `tests/Harbor.Ui.Framework.Tests`, `tests/Harbor.Architecture.Tests`,
  `contrib/apps/Harbor.App.Wpf`.

## Known limitations

- `DiffViewModelBase` has no shipped implementor; only a test double derives
  from it (#1875 tracks moving it down to `Harbor.Ui.Framework.ViewModels`).
- The `#188` edge to the preset catalogue is still open. It points at
  `Harbor.PolicyConfig` since #1625 moved the catalogue out of
  `Harbor.Application`, and it is the reason `Harbor.Application` is still in
  `Harbor.TestKit`'s transitive closure. The fix is a separate move of the
  catalogue to Domain.
- The assembly is `IsPackable` (`PackageId Harbor.Desktop.ViewModels`) but no
  Harbor package has been published to a feed yet, so the package identity is a
  forward-looking commitment, not a live contract.

## See also

- [ADR-021](../../docs/adr/ADR-021-desktop-abstractions-split.md) — the
  decision record.
- [`Harbor.Desktop.Abstractions`](../Harbor.Desktop.Abstractions/README.md) —
  the config-schema leaf.
- [docs/ARCHITECTURE_LAYERS.md](../../docs/ARCHITECTURE_LAYERS.md) — the layer
  matrix.
