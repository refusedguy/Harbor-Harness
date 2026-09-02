# Harbor.Scripting.Engines

Concrete `IScriptEngine` implementations. Runs JavaScript against a runtime — Jint (pure .NET interpreter) today, experimental SharpTS-based AOT-able runtime in research.

## Layer

Infrastructure — scripting pipeline layer. Depends on `Harbor.Scripting.Abstractions`.

## Dependencies

- `Harbor.Scripting.Abstractions`
- `Jint` (JavaScript interpreter)

## Public API

- `JintScriptEngine` — runs scripts via Jint interpreter
- `ScriptEngineBase` — shared base class (timeout enforcement, memory limits)

## Usage

```csharp
var engine = new JintScriptEngine(logger);
var result = await engine.EvaluateAsync(compiledScript, globals, options, ct);
```

## Pipeline position

```
Storage -> Compilation -> Engines -> Bridge -> Hosting
  ^ this project (layer 3: engines)
```

## See also

- [../../docs/SCRIPTING.md](../../docs/SCRIPTING.md)
- [../../docs/ARCHITECTURE_LAYERS.md](../../docs/ARCHITECTURE_LAYERS.md)
