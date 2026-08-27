# Harbor.CodeGen

Internal Roslyn source generator for Harbor. Emits `R` constant classes from AXIAM resource keys found in `Themes/Hds/*.axaml` files. Packaged as a Roslyn component (`IsRoslynComponentPackage=true`), not a runtime library.

## Layer

**Build-time tooling.** No runtime dependency; ships only as a `Microsoft.CodeAnalysis.CSharp` analyzer package consumed by the Avalonia app project during compilation.

## What's in it

| File | Purpose |
|------|---------|
| `ResourceKeyGenerator.cs` | `IIncrementalGenerator` that scans `.axaml` files for `x:Key` / `Key` attributes and emits `Harbor.App.Avalonia.Generated.R` constants. |

## Public API summary

- `ResourceKeyGenerator : IIncrementalGenerator` — registered via `<RoslynComponent>Harbor.CodeGen</RoslynComponent>` in consuming projects.
- Output namespace: `Harbor.App.Avalonia.Generated`
- Output type: `public static class R` with `internal const string` fields for each distinct key.

## Dependencies

| Package | Purpose |
|---------|---------|
| `Microsoft.CodeAnalysis.CSharp` (4.9.2) | Roslyn analyzer APIs (`IIncrementalGenerator`, `IncrementalGeneratorInitializationContext`). PrivateAssets=all. |

## Tests

No dedicated test project. Covered indirectly by the Avalonia app build and UI tests.

## Build

```bash
dotnet build src/Harbor.CodeGen/Harbor.CodeGen.csproj
```

## Known limitations

- Only processes `.axaml` files under `Themes/Hds/`; ignores other XAML dialects and non-AXIAM resource dictionaries.
- Silently swallows malformed XML (`catch {}`) — bad files are skipped without warning.
- Generated constants are `internal`, so only the Avalonia assembly can consume them.
