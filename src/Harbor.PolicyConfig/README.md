# Harbor.PolicyConfig

Harbor's configuration family, as its own Application-layer assembly: the
persisted config model (`HarborConfig` and its per-section records), the
atomic JSON store over `~/.harbor/config.json`, the API-key store, the
provider picker catalogue, the config normalizer, and the step-budget
projection. Extracted verbatim from `Harbor.Application/Configuration/` by
issue #1625 — the third and last piece of the `PolicyConfig` cohort of the
`Harbor.Application` split (variant A, ADR-018), after `Permissions` left as
[`Harbor.Permissions`](../Harbor.Permissions) under #1615 and `Onboarding`
left as [`Harbor.Onboarding`](../Harbor.Onboarding) under #1614.

## Public API

- `HarborConfig` — the canonical configuration record: identity (provider /
  model / agent as strongly-typed value objects), presentation, tooling, cost,
  compaction, the permissions section and the API-key map. `ToRaw()` /
  `RawConfigDto` are the persisted shape; `ConfigNormalizer.Normalize` turns
  one into the other.
- `IConfigStore` / `JsonConfigStore` — the store port and its JSON
  implementation: `LoadAsync`, `SaveAsync`, `UpdateAsync(Func<HarborConfig,
  HarborConfig>)`, `GetApiKeyAsync`. Writes are atomic (temp file + replace)
  and validate on write.
- `AuthStore` — API keys per provider, with a three-step resolution chain:
  config file → preset env var (`KILO_API_KEY`) → conventional env var
  (`<PROVIDER>_API_KEY`). `GetApiKeyAsync`, `SetApiKeyAsync`,
  `RemoveApiKeyAsync`, `ListApiKeysAsync`.
- `ProviderPresets` / `ProviderPresetCatalog` — the provider picker catalogue,
  projected from `providers/*.json` rather than hand-maintained (`#580`).
  `ProviderPresets.All`, `Find`, `GetNoAuth`; the catalog exposes
  `FromJson` (pure), `Load` and `FindProvidersDirectories` — the one directory
  walk that provider *registration* and the onboarding picker both use, so
  they cannot drift apart.
- `ConfigNormalizer` — the dual-schema compatibility point (canonical
  `provider` / `model` / `agent` win; the legacy `defaultProvider` /
  `defaultModel` aliases are the fallback).
- `AgentStepBudget` — projects the configured `maxSteps` onto an
  `AgentDefinition` (`Apply`), or answers `null` when the configuration asks
  for none.
- `PermissionConfigStoreAdapter` — the Application-side half of the #1615
  seam: it implements `Harbor.Application.Permissions.IPermissionConfigStore`
  over the whole-config `IConfigStore`, projecting the permissions section
  down.

## Wiring

No activation of its own: the assembly holds types, and the composition root
registers the implementations. `Harbor.Hosting/Modules/ConfigurationModule.cs`
registers `IConfigStore` → `JsonConfigStore` and `IPermissionConfigStore` →
`PermissionConfigStoreAdapter` as singletons, and loads the config once at
composition time into `HarborCompositionContext.Harbor`. There is no env var
and no feature flag.

The `providers/*.json` catalogue ships as embedded resources of THIS assembly
(`<EmbedProviders>true</EmbedProviders>`), because `ProviderPresetCatalog`
reads the resources off its own assembly — the property moved with the code
that reads it.

## Usage

```csharp
using Harbor.Application.Configuration; // namespace kept from the verbatim move

var store = new JsonConfigStore();                  // ~/.harbor/config.json
Result<HarborConfig> loaded = await store.LoadAsync(ct);
HarborConfig config = loaded.Value;

// The API key: config file → preset env var → conventional env var.
var auth = new AuthStore(store);
Result<string> key = await auth.GetApiKeyAsync("kilocode", ct);

// The provider picker catalogue.
IReadOnlyList<ProviderPresets.Preset> presets = ProviderPresets.All;
```

## Dependencies

- `Harbor.Abstractions` (+ transitively `Harbor.Abstractions.Contracts`) — the
  contract vocabulary: `AgentDefinition`, `ProviderId` / `ModelRef` /
  `AgentName`, `PermissionRule`, `ResultErrors`.
- `Harbor.Permissions` — one edge, and it is the #1615 seam: the
  `IPermissionConfigStore` port the permissions subdomain declares and this
  assembly implements. The direction is the point — the subdomain never binds
  `HarborConfig` or `IConfigStore`.
- Packages: `CSharpFunctionalExtensions` (the Result / Maybe railway),
  `Microsoft.Extensions.Logging.Abstractions` (`ILogger` and the BCL
  `[LoggerMessage]` source generator), `ZLinq` + `ZLinq.DropInGenerator` (the
  zero-allocation LINQ drop-in, the arrangement `Harbor.Application` uses).
- Referenced by: `Harbor.Hosting`, `Harbor.Onboarding`, `Harbor.Desktop.Abstractions`,
  `apps/Harbor.App.Cli`, `apps/Harbor.App.Avalonia`, `tests/Harbor.Config.Tests`,
  `tests/Harbor.Application.Tests`, `tests/Harbor.Core.Tests`,
  `tests/Harbor.App.Cli.Tests`, `tests/Harbor.Architecture.Tests` — pinned by
  `tests/Harbor.Architecture.Tests` (FullLayerMatrixTests row,
  Layer.Application).

## Known limitations

- The namespace stays `Harbor.Application.Configuration` (verbatim move, the
  same convention as #1613 Hooks and #1614 Onboarding), so consumers keep
  their `using`s. Do not "fix" it to `Harbor.PolicyConfig` without migrating
  every consumer and test projection at once.
- `ProviderPresetCatalog` filters the embedded resources by the `providers.`
  marker, and the manifest names the resources after the ROOT namespace
  (`Harbor.PolicyConfig.<id>.json`), so the embedded branch finds nothing
  today; the catalogue is served by the `providers/` directory walk. That is
  the behaviour `Harbor.Application` had before the move — unchanged, and
  tracked separately.
- The `IConfigStore` seam is deliberately wide: the permissions subdomain
  gets the narrow `IPermissionConfigStore` port, but every other consumer
  sees the whole config.
- `ConfigJsonContext` is `internal` — the AOT-safe serializer context is not
  part of the public surface.

## See also

- [`docs/ARCHITECTURE_LAYERS.md`](../../docs/ARCHITECTURE_LAYERS.md) §2 — the
  layer matrix row.
- [`docs/adr/ADR-018-harbor-application-split-variant-a.md`](../../docs/adr/ADR-018-harbor-application-split-variant-a.md)
  — the split decision this assembly is one piece of.
- [`src/Harbor.Permissions/README.md`](../Harbor.Permissions/README.md) — the
  sibling subdomain that owns the other half of the cohort.
- [`docs/CONFIGURATION.md`](../../docs/CONFIGURATION.md) — the config file this
  assembly reads and writes.
