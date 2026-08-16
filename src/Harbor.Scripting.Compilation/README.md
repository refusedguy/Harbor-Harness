# Harbor.Scripting.Compilation

Concrete `IScriptCompiler` implementations. Transpiles TypeScript -> JavaScript and/or compiles scripts to a cached form.

## Layer

Infrastructure — scripting pipeline layer. Depends on `Harbor.Scripting.Abstractions`.

## Dependencies

- `Harbor.Scripting.Abstractions`
- (TypeScript transpiler pulled in by Scripting.Engines — referenced transitively)

## Public API

- `TypeScriptScriptCompiler` — transpiles `.ts` -> `.js` via the SharpTS research
- `CachingScriptCompiler` — disk-cache decorator (cache key = source hash)
- `NoopScriptCompiler` — passthrough for `.js` files (no compilation needed)

## Usage

```csharp
IScriptCompiler compiler = new CachingScriptCompiler(
    new TypeScriptScriptCompiler(logger), cacheDir, logger);
var compiled = await compiler.CompileAsync(entry, ct);
```

## Pipeline position

```
Storage -> Compilation -> Engines -> Bridge -> Hosting
  ^ this project (layer 2: compilation)
```

## See also

- [../../docs/SCRIPTING.md](../../docs/SCRIPTING.md)
- [../../docs/ARCHITECTURE_LAYERS.md](../../docs/ARCHITECTURE_LAYERS.md)
