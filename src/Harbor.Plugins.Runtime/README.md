# Harbor.Plugins.Runtime

**THIN FACADE** kept for backward compatibility only. New code should depend on the layered projects (`Harbor.Plugins.Abstractions` / `Storage` / `Compilation` / `Instantiation` / `Registration` / `Hosting`) directly.

## Layer

Infrastructure — plugin pipeline layer. Facade over `Harbor.Plugins.Hosting` (which transitively pulls in all lower layers).

## Dependencies

- `Harbor.Plugins.Hosting` (facaded)
- `Harbor.Terminal.Abstractions` (panel contracts referenced by the loader path)
- `Microsoft.CodeAnalysis.CSharp`
- `Microsoft.Extensions.Logging.Abstractions`

## Public API

- `CsPluginLoader` — `[Obsolete]` wrapper around `PluginHost`/`PluginHostBuilder`
- `CompiledPlugin` — legacy record type returned by `CsPluginLoader`
- `PluginCompilationResult` — legacy result struct

## Usage

**Deprecated.** New code:

```csharp
var host = new PluginHostBuilder().WithSource(...).Build();
await host.LoadAllAsync(loadHost, ct);   // load host passed per call, see PluginHost.LoadAllAsync
```

Legacy (still works, emits `Obsolete` warning):

```csharp
var loader = new CsPluginLoader(loadHost, logger);   // (IPluginLoadHost, ILogger<CsPluginLoader>, ...)
await loader.DiscoverAndLoadAsync(ct);
```

## Pipeline position

```
Storage -> Compilation -> Instantiation -> Registration -> Hosting
  ^ this project (legacy facade, deprecated)
```

## See also

- [../../docs/PLUGIN_SYSTEM.md](../../docs/PLUGIN_SYSTEM.md)
- [../../docs/ARCHITECTURE_LAYERS.md](../../docs/ARCHITECTURE_LAYERS.md)
- [../../samples/plugins/](../../samples/plugins/) — sample plugins (FileTree, GitTools, TodoWrite, WebSearch)
