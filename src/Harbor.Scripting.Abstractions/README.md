# Harbor.Scripting.Abstractions

Scripting pipeline contracts. Defines `IScriptEngine`, `IScriptStore`, `IScriptCompiler`, plus the data types that flow between them (`ScriptEntry`, `ScriptGlobals`, `ScriptEngineOptions`).

## Layer

Infrastructure — scripting pipeline layer. Depends only on `Harbor.Abstractions` (Domain) — never `Harbor.Core`.

## Dependencies

- `Harbor.Abstractions` (Domain)

## Public API

- `IScriptEngine` — engine contract (`EvaluateAsync(script, globals, options, ct)`)
- `IScriptStore` — storage contract (enumerates `ScriptEntry` records)
- `IScriptCompiler` — compilation contract (`ScriptEntry` -> `CompiledScript`)
- `ScriptEngineOptions` — resource limits (timeout, memory, eval depth)
- `ScriptGlobals` — the `Harbor` global object exposed to scripts (registry of safe APIs)
- `ScriptEntry` — immutable record returned by `IScriptStore` (path + content + hash)

## Usage

Implement these contracts in your own scripting layers, or use the Harbor-provided defaults.

## Pipeline position

```
Storage -> Compilation -> Engines -> Bridge -> Hosting
  ^ this project (contracts shared by all layers)
```

## See also

- [../../docs/SCRIPTING.md](../../docs/SCRIPTING.md)
- [../../docs/ARCHITECTURE_LAYERS.md](../../docs/ARCHITECTURE_LAYERS.md)
