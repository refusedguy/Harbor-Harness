# Harbor.Desktop.Abstractions

The config schema, the config-store ports, the cross-view-model messages and
the shared models/enums that every Harbor desktop app (Avalonia, WPF, MAUI,
Blazor) binds. It is a **Presentation** leaf
([docs/ARCHITECTURE_LAYERS.md](../../docs/ARCHITECTURE_LAYERS.md) §1), and it
has exactly one Harbor reference.

## What is in it

- **Configuration** (`Configuration/`): `IAppConfigStore<T>` /
  `ICommonConfigStore` plus the shared config DTOs (`AppConfigBase`,
  `CommonConfig`, `CompositeConfig<T>`). The JSON+filesystem stores
  (`JsonAppConfigStore<T>`, `JsonCommonConfigStore`) and the AOT
  `ConfigJsonContext` they need live in `Harbor.Hosting/Configuration`, because
  this project is `IsPackable` (`PackageId: Harbor.Desktop.Abstractions`)
  and the layer matrix calls it **Presentation** — which
  `PresentationCapabilityRules` bars from `System.IO.File*` in any case. See
  `tests/Harbor.Architecture.Tests/DesktopAbstractionsLeafTakesNoIoRules.cs`.
- **Messages** (`Messages/CrossVmMessages.cs`): typed cross-view-model events
  (`ModelPickedMessage`, `OnboardingCompletedMessage`).
- **Models** (`Models/`): `ThemeKind`, `ToastKind`, `SessionDotState` +
  `SessionDotStates`, `CommandPaletteItem`.

## What is not in it

Until #1899 this assembly also held the concrete view-models and their
abstract bases, nine project references and an 18-assembly dependency closure —
a hub wearing a leaf's name. The view-models are now in
[`Harbor.Desktop.ViewModels`](../Harbor.Desktop.ViewModels/README.md), and what
is left here is the part the name always described.

## Public API

- `Harbor.Desktop.Abstractions.Configuration.AppConfigBase` — abstract base for
  a per-app config record: `AppId`, `ConfigFileName`, `ConfigDirectory`,
  `ConfigFilePath`.
- `Harbor.Desktop.Abstractions.Configuration.CommonConfig` — the settings every
  app shares: `ConfigVersion`, `OnboardingCompleted`, `Theme`, `ApiKeys`,
  `DefaultProvider`, `DefaultModel`, `DefaultAgent`, `HasDefaultPair`,
  `StorageBackend`, `StoragePath`, `LogLevel`, `EnableFileLogging`,
  `MaxLogFiles`, `PermissionMode`, `AlwaysAllowTools`, `AlwaysDenyTools`,
  `EnablePlugins`, `PluginDirectories`, `EnableScripting`, `HttpProxy`,
  `HttpTimeoutSeconds`, `UserAgent`, `CompactionReserveTokens`,
  `CompactionKeepRecentTokens`, `CompactionTailTurns`, `ConfigDirectory`,
  `ConfigFileName`, `ConfigFilePath`, `HarborHome`.
- `Harbor.Desktop.Abstractions.Configuration.CompositeConfig<TAppConfig>` —
  the immutable pair of a `CommonConfig` and an app-specific config:
  `Common`, `App`, `AppId`, `EffectiveStorageBackend(envOverride)`.
- `Harbor.Desktop.Abstractions.Configuration.IAppConfigStore<T>` —
  `LoadAsync`, `SaveAsync`, `UpdateAsync`.
- `Harbor.Desktop.Abstractions.Configuration.ICommonConfigStore` — the same
  three members over `CommonConfig`.
- `Harbor.Desktop.Abstractions.Messages.ModelPickedMessage`,
  `Harbor.Desktop.Abstractions.Messages.OnboardingCompletedMessage`.
- `Harbor.Desktop.Abstractions.Models.ThemeKind` (`Dark`, `Light`, `System`),
  `ToastKind` (`Info`, `Success`, `Warning`, `Error`),
  `SessionDotState` (`Idle`, `Running`, `Thinking`, `Queued`, `Done`, `Error`),
  `SessionDotStates.FromStatus(SessionStatus)`,
  `CommandPaletteItem` (`Title`, `Subtitle`, `Icon`, `Action`, `Create`).

## Wiring

None of its own: the ports are resolved out of DI by the composition root
(`Harbor.Hosting/Modules/ConfigurationModule.cs`), and the JSON
implementations live there too. `Harbor.Ui.Framework.Abstractions`'s
`ICommonConfigModelRefReader` is the narrow read-only half of the same file and
is bridged onto `ICommonConfigStore` by a per-platform adapter (see
[ADR-009](../../docs/adr/DECISIONS.md)).

## Usage

```csharp
using Harbor.Desktop.Abstractions.Configuration;

// The composition root resolves the port; the app derives its own record.
public sealed record CliConfig : AppConfigBase
{
    public override string AppId => "cli";

    public override string ConfigFileName => "cli.json";
}
```

## Dependencies

- `Harbor.Abstractions.Contracts` — `SessionStatus` and `AgentName.Fallback`,
  the leaf's only two Harbor types.
- Referenced by: `apps/Harbor.App.Cli`, `apps/Harbor.App.Avalonia`,
  `src/Harbor.Hosting`, `src/Harbor.Desktop.Shared`,
  `src/Harbor.Desktop.ViewModels`, `tests/Harbor.Architecture.Tests`,
  `contrib/apps/Harbor.App.{Blazor,Maui,Wpf}`.

## Known limitations

- `ThemeKind` and `ToastKind` are not referenced anywhere in the tree today
  (`ToastKind` duplicates the one in `Harbor.Ui.Framework.Services`). They are
  published surface; removing them is a separate decision.
- The config STORES are not here — they are persistence, and they live in
  `Harbor.Hosting`. Implement `ICommonConfigStore` yourself if you need a
  different store.
- `CommonConfig` resolves `~/.harbor` through
  `Environment.GetFolderPath` at first access. That is path resolution, not
  persistence, and it is deliberate.

## See also

- [ADR-021](../../docs/adr/ADR-021-desktop-abstractions-split.md) — the
  decision record for the split.
- [`Harbor.Desktop.ViewModels`](../Harbor.Desktop.ViewModels/README.md) — the
  view-model layer that used to live here.
- [docs/CONFIGURATION.md](../../docs/CONFIGURATION.md) — the config guide.
