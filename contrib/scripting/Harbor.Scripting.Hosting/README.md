# Harbor.Scripting.Hosting

Composition root for the scripting pipeline. Orchestrates: `IScriptStore` -> `IScriptCompiler` -> `IScriptEngine` (with bridge globals wired in).

## Layer

Infrastructure — scripting pipeline layer. Depends on all 5 sibling scripting layers + `Harbor.Abstractions`.

## Dependencies

- `Harbor.Scripting.Abstractions`
- `Harbor.Scripting.Storage`
- `Harbor.Scripting.Compilation`
- `Harbor.Scripting.Engines`
- `Harbor.Scripting.Bridge`
- `Microsoft.Extensions.Logging.Abstractions`

## Public API

- `ScriptHost` — composes engine + store + compiler into a load/evaluate pipeline
- `ScriptHostOptions` — `ContinueOnFailure`, `ScriptRoot`, etc.
- `ScriptInstance` — a loaded script with evaluation metadata

## Usage

```csharp
var host = new ScriptHostBuilder()
    .WithStore(new FileSystemScriptStore(scriptsDir, logger))
    .WithCompiler(new TypeScriptScriptCompiler(logger))
    .WithEngine(new JintScriptEngine(logger))
    .WithBridge(new ScriptHostBridge(toolRegistry, eventBus, logger))
    .WithOptions(o => o.ContinueOnFailure = true)
    .Build();

await host.LoadAllAsync(ct);
```

## Pipeline position

```
Storage -> Compilation -> Engines -> Bridge -> Hosting
  ^ this project (layer 5: hosting / orchestrator)
```

## See also

- [../../docs/SCRIPTING.md](../../docs/SCRIPTING.md)
- [../../docs/ARCHITECTURE_LAYERS.md](../../docs/ARCHITECTURE_LAYERS.md)
