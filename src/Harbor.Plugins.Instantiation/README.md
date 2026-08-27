# Harbor.Plugins.Instantiation

Concrete `IPluginInstantiator` implementations. Activates `IPlugin` types from a `CompiledPluginAssembly` via reflection.

## Layer

Infrastructure — plugin pipeline layer. Depends on `Harbor.Plugins.Abstractions` (and indirectly `Harbor.Abstractions` for `IPlugin`).

## Dependencies

- `Harbor.Plugins.Abstractions`

## Public API

- `ReflectionPluginInstantiator` — parameterless ctor; `Instantiate(CompiledPluginAssembly)` returns `Result<IReadOnlyList<LoadedPlugin>>`, reflecting over the assembly for public `IPlugin` types with parameterless ctors
- `PluginLifecycle` — static `Initialize` / `ShutdownAsync` helpers returning `Result`

## Usage

```csharp
var instantiator = new ReflectionPluginInstantiator();
var result = instantiator.Instantiate(compiled);   // Result<IReadOnlyList<LoadedPlugin>>
if (result.IsSuccess)
    PluginLifecycle.Initialize(result.Value[0], pluginContext);
```

## Pipeline position

```
Storage -> Compilation -> Instantiation -> Registration -> Hosting
  ^ this project (layer 3: instantiation)
```

## See also

- [../../docs/PLUGIN_SYSTEM.md](../../docs/PLUGIN_SYSTEM.md)
- [../../docs/ARCHITECTURE_LAYERS.md](../../docs/ARCHITECTURE_LAYERS.md)
- [../../samples/plugins/](../../samples/plugins/) — sample plugins (FileTree, GitTools, TodoWrite, WebSearch)
