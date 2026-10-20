# Harbor.Hosting

The **composition root** for all Harbor applications. `Registration.AddHarbor(...)` builds the entire DI graph; apps only pass `HarborComposeOptions` and call `AddHarbor`. No service registrations live in app projects (`Cli`, `Avalonia`, `Wpf`, `Maui`, `Blazor`).

## Layer

**Composition Root (composition root).** The matrix gives `Harbor.Hosting` its own `CompositionRoot` layer — not Infrastructure — and that layer is unrestricted: it may reference Domain, Application, Infrastructure and Presentation alike. Owns feature flags (`HARBOR_WITH_PLUGINS`, `HARBOR_WITH_SPECTRE_TUI`, `HARBOR_WITH_ALL_PROVIDERS`) and the fixed call-order module registration.

## What's in it

| File | Purpose |
|------|---------|
| `Registration.cs` | `AddHarbor(...)` — the single entry point for wiring Harbor services. |
| `HarborComposeOptions.cs` | App-specific configuration passed into composition (storage backend, TUI renderer, event bus middleware, config paths). |
| `HarborCompositionContext.cs` | Runtime bag holding resolved options, logger factory, common config, event bus, and registries. |
| `HarborFeatureSet.cs` | Record capturing which optional feature flags are active. |
| `Modules/*.cs` | Individual DI modules: `CoreModule`, `IntelligenceModule`, `IpcModule`, `TelemetryModule`, `TuiModule`, `StorageModule`, `ConfigurationModule`, `RegistriesModule`, `ToolsCatalog`, `ProviderFactories`, `JsonProviderDiscovery`, `ConfigAuthResolver`. CS plugins are served out-of-process (#1055s3), so no module compiles them in-process. |

## Public API summary

- **`Registration.AddHarbor(IServiceCollection, HarborComposeOptions, ILoggerFactory?)`**: registers the full Harbor graph. Call order is fixed and architecture-tested.
- **`HarborComposeOptions`**: `HarborDir`, `DefaultStorageBackend`, `DefaultTuiRenderer`, `EventBusMiddlewares`, `EventBusScrollback`, `ConfigPath`, `AgentModelSource`, `ProviderFlavor`, `ToolSetKind`.
- **`HarborCompositionContext`**: resolved options + `Common` config + `Harbor` config + `EventBus` + `Registries`. `EventBus` and `Registries` are assigned exactly once — by `AddHarborConfiguration` and `AddHarborRegistries` — and a module that reads one before its owner ran gets an `InvalidOperationException` naming the member and the module, not a null (#562). `HarborRegistries` takes its four registries as constructor arguments, so a half-built bundle cannot be represented.
- **Feature flags**: controlled via MSBuild properties (`HarborWithPlugins`, `HarborWithSpectreTui`, `HarborWithAllProviders`, `HarborWithAllTools`) → `#if` constants inside modules.

## Wiring

The composition root reads two environment variables for storage:

- `HARBOR_STORAGE` picks the session-store backend (`memory`, `jsonl`, `sqlite`).
- `HARBOR_SESSION_TIER` picks the tier the JSONL logs are written in: `local` (the default, nothing is scrubbed) or `keyless`. In the keyless tier the store scrubs provider credentials out of every text-bearing field before the message reaches the disk, so the log can be committed and replayed without a provider key (#1740). The tier is read in `Modules/SessionLogEnvironment.cs`, and an unknown value fails the composition: the default tier is the UNSCRUBBED one, so a typo in the variable name must not fall back to it.

## Dependencies

| Package | Purpose |
|---------|---------|
| `Microsoft.Extensions.DependencyInjection.Abstractions` | DI container contracts |
| `Microsoft.Extensions.Logging.Abstractions` | Logging contracts |
| `Microsoft.Extensions.Configuration.Abstractions` | Configuration contracts |
| `Microsoft.Extensions.Http` | `IHttpClientFactory` for providers |

## Tests

`tests/Harbor.Hosting.Tests/` — covers `AddHarbor` call order and `HarborComposeOptions` defaults, and composes the DI modules in a deliberately wrong order to pin that a reorder fails loudly (`CompositionContextInitializationTests`).

## Build

```bash
dotnet build src/Harbor.Hosting/Harbor.Hosting.csproj
```

## Known limitations

- All app hosts must call `AddHarbor` exactly once; module ordering is not configurable. A module invoked out of order now throws at composition time instead of composing against an unassigned member.
- Feature flags are compile-time (`#if`), not runtime — switching a flag requires a rebuild.
