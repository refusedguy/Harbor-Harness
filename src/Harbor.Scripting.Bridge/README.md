# Harbor.Scripting.Bridge

Bridge between the scripting engine and the Harbor host. Exposes Harbor APIs (tools, sessions, events) to scripts as the `Harbor` global object.

## Layer

Infrastructure — scripting pipeline layer. Depends on `Harbor.Scripting.Abstractions` + `Harbor.Abstractions`.

## Dependencies

- `Harbor.Scripting.Abstractions`
- `Harbor.Abstractions` (for the APIs exposed to scripts)

## Public API

- `ScriptHostBridge` — implements `ScriptGlobals`, wraps Harbor services
- `ScriptToolAdapter` — adapts scripts that define a tool into `ITool` instances
- `ScriptEventSink` — lets scripts subscribe to `AgentEvent`s

## Usage

```csharp
var bridge = new ScriptHostBridge(toolRegistry, eventBus, logger);
var globals = bridge.BuildGlobals();
var result = await engine.EvaluateAsync(script, globals, options, ct);
```

## Pipeline position

```
Storage -> Compilation -> Engines -> Bridge -> Hosting
  ^ this project (layer 4: bridge — wires engine to host)
```

## See also

- [../../docs/SCRIPTING.md](../../docs/SCRIPTING.md)
- [../../docs/ARCHITECTURE_LAYERS.md](../../docs/ARCHITECTURE_LAYERS.md)
