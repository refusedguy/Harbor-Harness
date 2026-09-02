# Harbor.Scripting.Storage

Concrete `IScriptStore` implementations. Reads `.ts` / `.js` script files from local filesystem or in-memory.

## Layer

Infrastructure — scripting pipeline layer. Depends only on `Harbor.Scripting.Abstractions`.

## Dependencies

- `Harbor.Scripting.Abstractions`
- `Microsoft.Extensions.Logging.Abstractions`

## Public API

- `FileSystemScriptStore` — reads `.ts`/`.js` from a local filesystem directory
- `InMemoryScriptStore` — for tests + ephemeral scripts

## Usage

```csharp
var store = new FileSystemScriptStore(scriptsDir, logger);
await foreach (var entry in store.DiscoverAsync(ct)) { ... }
```

## Pipeline position

```
Storage -> Compilation -> Engines -> Bridge -> Hosting
  ^ this project (layer 1: storage)
```

## See also

- [../../docs/SCRIPTING.md](../../docs/SCRIPTING.md)
- [../../docs/ARCHITECTURE_LAYERS.md](../../docs/ARCHITECTURE_LAYERS.md)
