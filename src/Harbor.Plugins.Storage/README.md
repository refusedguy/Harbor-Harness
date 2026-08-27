# Harbor.Plugins.Storage

Concrete `IPluginSource` implementations. Reads `.cs` plugin source files from various backends.

## Layer

Infrastructure — plugin pipeline layer. Depends only on `Harbor.Plugins.Abstractions`. Knows nothing about compilation, instantiation, registration, or hosting.

## Dependencies

- `Harbor.Plugins.Abstractions`
- `Microsoft.Extensions.Logging.Abstractions`

## Public API

- `FileSystemPluginSource` — reads `.cs` files from local directories
- `InMemoryPluginSource` — for tests + ephemeral plugins
- `CompositePluginSource` — fan-in over multiple sources
- `EmbeddedResourcePluginSource` — reads `.cs` from assembly resources

## Usage

```csharp
var source = new FileSystemPluginSource(
    new[] { Path.Combine(AppContext.BaseDirectory, "plugins") },
    logger);
await foreach (var script in source.GetScriptsAsync(ct)) { ... }
```

## Pipeline position

```
Storage -> Compilation -> Instantiation -> Registration -> Hosting
  ^ this project (layer 1: storage)
```

## See also

- [../../docs/PLUGIN_SYSTEM.md](../../docs/PLUGIN_SYSTEM.md)
- [../../docs/ARCHITECTURE_LAYERS.md](../../docs/ARCHITECTURE_LAYERS.md)
- [../../samples/plugins/](../../samples/plugins/) — sample plugins (FileTree, GitTools, TodoWrite, WebSearch)
