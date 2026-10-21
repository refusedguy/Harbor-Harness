# Harbor.Plugins.Abstractions

Contracts shared across the entire plugin pipeline. Contains the leaf-level interfaces (`IPluginSource`, `IPluginCompiler`, `IPluginInstantiator`, `IPluginRegistrar`, `IPluginLoadHost`) and the immutable data types that flow between them (`PluginScript`, `CompiledPluginAssembly`, `LoadedPlugin`, `CompilationResult`).

## Layer

Application — the plugin CONTRACT layer, and the matrix places it in **Application**, not Infrastructure: the six plugin *implementations* below it (Compilation, Instantiation, Registration, Hosting, Runtime, Storage) are Infrastructure, and a Domain/Application row may not reference them. References `Harbor.Abstractions` + `Harbor.Terminal.Abstractions` (for the panel/TUI-plugin contracts surfaced on `IPluginLoadHost`). Never references `Harbor.Application` / `Harbor.Registries`.

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
- `IPluginRegistrar` — registration contract (`LoadedPlugin` -> wires into host registries).
  It answers with the live RUN it created (`Result<IPluginRun>`), so the registration
  can be taken back later (#1737)
- `IPluginLoadHost` — host-side sink (called by the registrar with new plugins).
  Every `Register*` door reports the OUTCOME: `Success` means the registration
  took effect and is observable, `Failure` means it was not registered and says
  why. A host that does not carry a door MUST refuse it, not accept it (#1758).
- `IReversiblePluginLoadHost` — the OPTIONAL undo half of the load host: the
  `Unregister*` methods that take a registration back. A host that implements it
  can stop a run; a host that does not says so instead of pretending (#1737)
- `IPluginRun` — the live run of one plugin: how it came to be, and the stop
  that takes back exactly what it registered and shuts the plugin down (#1737)
- `PluginRunMode` — `Run` (start the current definition) or `Update` (replace the
  running one) (#1737)
- `PluginAxisRegistry` — the single registry of plugin extension axes (marker + host door per axis)
- `PluginCompatibility` — the compatibility rule: may a plugin that declares a
  Harbor floor run against the Harbor that is running? The registrar applies it
  to the in-code floor (#1721), the host applies it to the manifest's floor
  (#1739)
- `PluginManifest` — a plugin's declaration as DATA: version, Harbor floor, capabilities and
  extension axes, read from a JSON file next to the plugin without loading its code (#1739)
- `IPluginDependencyResolver` — says whether a dependency a plugin declared is ready right now (#1733)
- `PendingPlugin` — a plugin that is loaded but not activated, and the dependencies it waits for (#1733)
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
