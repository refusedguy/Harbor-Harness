# Harbor.Plugins.Instantiation

Concrete `IPluginInstantiator` implementations. Activates `IPlugin` types from a `CompiledPluginAssembly` via reflection.

## Layer

Infrastructure — plugin pipeline layer. Depends on `Harbor.Plugins.Abstractions` (and indirectly `Harbor.Abstractions` for `IPlugin`).

## Dependencies

- `Harbor.Plugins.Abstractions`

## Public API

- `ReflectionPluginInstantiator` — reflects over `CompiledPluginAssembly`, finds `IPlugin` types with parameterless ctors, activates them
- `PluginLifecycle` — `Initialize` / `ShutdownAsync` helpers

## Usage

```csharp
var instantiator = new ReflectionPluginInstantiator(logger);
await foreach (var loaded in instantiator.InstantiateAsync(compiled, ct)) { ... }
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
