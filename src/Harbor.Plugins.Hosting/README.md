# Harbor.Plugins.Hosting

Composition root for the layered plugin runtime. Orchestrates the full pipeline: `IPluginSource` -> `IPluginCompiler` -> `IPluginInstantiator` -> `IPluginRegistrar`.

## Layer

Infrastructure — plugin pipeline layer. Depends on all four lower plugin layers + `Harbor.Plugins.Abstractions`.

## Dependencies

- `Harbor.Plugins.Abstractions`
- `Harbor.Plugins.Storage`
- `Harbor.Plugins.Compilation`
- `Harbor.Plugins.Instantiation`
- `Harbor.Plugins.Registration`
- `Microsoft.Extensions.Logging.Abstractions`

## Public API

- `PluginHost` — iterates source, compiles, instantiates, registers
- `PluginHostBuilder` — fluent builder; the only way to construct `PluginHost`
- `PluginHostOptions` — `ContinueOnError`, `PluginRoot`, `Dependencies`, `HarborVersion`, etc.
- `PluginHostLocator` — probes for the out-of-process host binary next to the CLI
- `PluginRouteAxisRegistry` — what the out-of-process route does with each
  extension axis, and why (issue #1723); the host's refusal messages are built
  from it, and `tests/Harbor.Architecture.Tests/ExtensionAxisRouteCarriageRule.cs`
  grades it against the host's code
- `PluginAxisCarriage` — `Carried` / `Refused` / `ClosedSeam`
- `PluginDependencyResolver` — the default `IPluginDependencyResolver`: a table of
  named capabilities, each with a readiness probe (#1733)

`PluginHost` also holds the manifest gate (#1739): a plugin's manifest is read
as data at discovery, and a manifest the host cannot read, or one that asks for
a Harbor newer than the running one, is refused BEFORE the plugin is compiled.

## The plugin life: define, run, stop, update (#1737)

A plugin has a **definition** and a **run**, and the host keeps the two apart:

| Operation | What it does |
|---|---|
| define | the host learns a plugin: the immutable package it met — source, version, manifest. |
| run | the definition is started: the plugin is initialized and registers its contributions. |
| stop | the live run is stopped: what it registered is taken back and the plugin is shut down. The DEFINITION is kept. |
| update | the running version is REPLACED: the old run is stopped first, then the new one starts. |
| undefine | the definition is forgotten. Refused while a run is live. |

`PluginHost.Definitions` lists the definitions; `PluginHost.Inventory` is the
machine-readable composition, and its `phase` says where each plugin is
(`pending`, `active`, `failed`, or `stopped` — the one a load-only document
cannot have).

The load is idempotent: a plugin whose source did not change is left alone
(the live run IS that plugin), and a plugin whose source changed is an update.
A lifecycle gate serializes the operations, so an update cannot race a stop.

## Usage

```csharp
var host = new PluginHostBuilder()
    .WithSource(new FileSystemPluginSource(pluginRoot, logger))
    .WithCompiler(new RoslynPluginCompiler(references))
    .WithInstantiator(new ReflectionPluginInstantiator(logger))
    .WithRegistrar(new SafePluginRegistrar(new PluginRegistrar(logger), logger))
    .WithOptions(o => o.ContinueOnError = true)
    .Build(logger);

await host.LoadAllAsync(myHarborLoadHost, ct);   // IPluginLoadHost passed per call
```

## Pipeline position

```
Storage -> Compilation -> Instantiation -> Registration -> Hosting
  ^ this project (layer 5: hosting / orchestrator)
```

## See also

- [../../docs/PLUGIN_SYSTEM.md](../../docs/PLUGIN_SYSTEM.md)
- [../../docs/ARCHITECTURE_LAYERS.md](../../docs/ARCHITECTURE_LAYERS.md)
- [../../samples/plugins/](../../samples/plugins/) — sample plugins (FileTree, GitTools, TodoWrite, WebSearch)
