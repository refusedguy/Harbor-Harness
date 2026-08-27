# Harbor.Plugins.Abstractions

Contracts shared across the entire plugin pipeline. Contains the leaf-level interfaces (`IPluginSource`, `IPluginCompiler`, `IPluginInstantiator`, `IPluginRegistrar`, `IPluginLoadHost`) and the immutable data types that flow between them (`PluginScript`, `CompiledPluginAssembly`, `LoadedPlugin`, `CompilationResult`).

## Layer

Infrastructure — plugin pipeline layer. References `Harbor.Abstractions` + `Harbor.Terminal.Abstractions` (for the panel/TUI-plugin contracts surfaced on `IPluginLoadHost`). Never references `Harbor.Core`.

## Dependencies

- `Harbor.Abstractions`
- `Harbor.Terminal.Abstractions` (panel contracts)
- `Microsoft.CodeAnalysis.CSharp` (for `Diagnostic` type)
- `Microsoft.Extensions.Logging.Abstractions`
- `Microsoft.Extensions.DependencyInjection.Abstractions`
- `Microsoft.Extensions.Configuration.Abstractions`

## Public API

- `IPluginSource` — storage contract (enumerates `PluginScript` records)
- `IPluginCompiler` — compilation contract (`PluginScript` -> `CompilationResult`)
- `IPluginInstantiator` — instantiation contract (`CompiledPluginAssembly` -> `LoadedPlugin`)
- `IPluginRegistrar` — registration contract (`LoadedPlugin` -> wires into host registries)
- `IPluginLoadHost` — host-side sink (called by the registrar with new plugins)
- `PluginScript` — immutable wrapper around a single `.cs` source file (path + content + hash)
- `CompiledPluginAssembly` — loaded assembly + source hash
- `LoadedPlugin` — live `IPlugin` instance + metadata
- `CompilationResult` — result struct returned by `IPluginCompiler`

## Usage

Implement these contracts in your own pipeline layers, or use the Harbor-provided defaults:

```csharp
public sealed class MyPluginSource : IPluginSource
{
    public IAsyncEnumerable<PluginScript> GetScriptsAsync(CancellationToken ct = default) { ... }
}
```

## Pipeline position

```
Storage -> Compilation -> Instantiation -> Registration -> Hosting
  ^ this project (contracts shared by all layers)
```

## See also

- [../../docs/PLUGIN_SYSTEM.md](../../docs/PLUGIN_SYSTEM.md)
- [../../docs/ARCHITECTURE_LAYERS.md](../../docs/ARCHITECTURE_LAYERS.md)
- [../../samples/plugins/](../../samples/plugins/) — sample plugins (FileTree, GitTools, TodoWrite, WebSearch)
