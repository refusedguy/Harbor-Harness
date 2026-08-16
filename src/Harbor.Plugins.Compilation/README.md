# Harbor.Plugins.Compilation

Concrete `IPluginCompiler` implementations. Compiles `.cs` source into in-memory assemblies via Roslyn.

## Layer

Infrastructure — plugin pipeline layer. Depends on `Harbor.Plugins.Abstractions`. Pulls in `Microsoft.CodeAnalysis.CSharp` for the Roslyn implementation.

## Dependencies

- `Harbor.Plugins.Abstractions`
- `Microsoft.CodeAnalysis.CSharp`
- `Microsoft.Extensions.Logging.Abstractions`

## Public API

- `RoslynPluginCompiler` — compiles CS source in-memory via Roslyn
- `CachingCompiler` — disk-cache decorator over any IPluginCompiler (cache key = source hash)
- `PluginAssemblyReferences` — collects MetadataReference snapshot for Roslyn

## Usage

```csharp
IPluginCompiler compiler = new CachingCompiler(
    new RoslynPluginCompiler(referenceCollector, logger),
    cacheDir, logger);
var result = await compiler.CompileAsync(script, ct);
```

## Pipeline position

```
Storage -> Compilation -> Instantiation -> Registration -> Hosting
  ^ this project (layer 2: compilation)
```

## See also

- [../../docs/PLUGIN_SYSTEM.md](../../docs/PLUGIN_SYSTEM.md)
- [../../docs/ARCHITECTURE_LAYERS.md](../../docs/ARCHITECTURE_LAYERS.md)
- [../../samples/plugins/](../../samples/plugins/) — sample plugins (FileTree, GitTools, TodoWrite, WebSearch)
