# Harbor.Plugins.Runtime

**THIN FACADE** kept for backward compatibility only. New code should depend on the layered projects (`Harbor.Plugins.Abstractions` / `Storage` / `Compilation` / `Instantiation` / `Registration` / `Hosting`) directly.

## Layer

Infrastructure — plugin pipeline layer. Facade over `Harbor.Plugins.Hosting` (which transitively pulls in all lower layers).

## Dependencies

- `Harbor.Plugins.Hosting` (facaded)
- `Harbor.Tui.Abstractions` (legacy logging reference)
- `Microsoft.CodeAnalysis.CSharp`
- `Microsoft.Extensions.Logging.Abstractions`

## Public API

- `CsPluginLoader` — `[Obsolete]` wrapper around `PluginHostBuilder`/`PluginHost`
- `CompiledPlugin` — legacy record type returned by `CsPluginLoader`
- `PluginCompilationResult` — legacy result struct

## Usage

**Deprecated.** New code:

```csharp
var host = new PluginHostBuilder().WithSource(...).Build();
await host.LoadAllAsync(ct);
```

Legacy (still works, emits `Obsolete` warning):

```csharp
var loader = new CsPluginLoader(logger);
await loader.DiscoverAndLoadAsync(loadHost, ct);
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
