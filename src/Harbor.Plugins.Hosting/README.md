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
- `PluginHostOptions` — `ContinueOnError`, `PluginRoot`, etc.

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
