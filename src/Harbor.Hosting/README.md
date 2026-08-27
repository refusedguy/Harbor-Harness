# Harbor.Hosting

The **composition root** for all Harbor applications. `Registration.AddHarbor(...)` builds the entire DI graph; apps only pass `HarborComposeOptions` and call `AddHarbor`. No service registrations live in app projects (`Cli`, `Avalonia`, `Wpf`, `Maui`, `Blazor`).

## Layer

**Infrastructure (composition root).** Owns feature flags (`HARBOR_WITH_PLUGINS`, `HARBOR_WITH_SPECTRE_TUI`, `HARBOR_WITH_ALL_PROVIDERS`) and the fixed call-order module registration.

## What's in it

| File | Purpose |
|------|---------|
| `Registration.cs` | `AddHarbor(...)` — the single entry point for wiring Harbor services. |
| `HarborComposeOptions.cs` | App-specific configuration passed into composition (storage backend, TUI renderer, event bus middleware, config paths). |
| `HarborCompositionContext.cs` | Runtime bag holding resolved options, logger factory, common config, event bus, and registries. |
| `HarborFeatureSet.cs` | Record capturing which optional feature flags are active. |
| `Modules/*.cs` | Individual DI modules: `CoreModule`, `IntelligenceModule`, `IpcModule`, `TelemetryModule`, `TuiModule`, `PluginLoadHostAdapter`, `StorageModule`, `ConfigurationModule`, `RegistriesModule`, `ToolsCatalog`, `ProviderFactories`, `JsonProviderDiscovery`, `ConfigAuthResolver`. |

## Public API summary

- **`Registration.AddHarbor(IServiceCollection, HarborComposeOptions, ILoggerFactory?)`**: registers the full Harbor graph. Call order is fixed and architecture-tested.
- **`HarborComposeOptions`**: `HarborDir`, `DefaultStorageBackend`, `DefaultTuiRenderer`, `EventBusMiddlewares`, `EventBusScrollback`, `ConfigPath`, `AgentModelSource`, `ProviderFlavor`, `ToolSetKind`.
- **`HarborCompositionContext`**: resolved options + `Common` config + `Harbor` config + `EventBus` + `Registries`.
- **Feature flags**: controlled via MSBuild properties (`HarborWithPlugins`, `HarborWithSpectreTui`, `HarborWithAllProviders`, `HarborWithAllTools`) → `#if` constants inside modules.

## Dependencies

| Package | Purpose |
|---------|---------|
| `Microsoft.Extensions.DependencyInjection.Abstractions` | DI container contracts |
| `Microsoft.Extensions.Logging.Abstractions` | Logging contracts |
| `Microsoft.Extensions.Configuration.Abstractions` | Configuration contracts |
| `Microsoft.Extensions.Http` | `IHttpClientFactory` for providers |

## Tests

`tests/Harbor.Hosting.Tests/` — covers `AddHarbor` call order and `HarborComposeOptions` defaults.

## Build

```bash
dotnet build src/Harbor.Hosting/Harbor.Hosting.csproj
```

## Known limitations

- All app hosts must call `AddHarbor` exactly once; module ordering is not configurable.
- Feature flags are compile-time (`#if`), not runtime — switching a flag requires a rebuild.
