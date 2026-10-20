# Harbor.CodeGen

Internal Roslyn source generator for Harbor. Emits `R` constant classes from AXIAM resource keys found in `Themes/Hds/*.axaml` files. Packaged as a Roslyn component (`IsRoslynComponentPackage=true`), not a runtime library.

## Layer

**Build-time tooling.** No runtime dependency; ships only as a `Microsoft.CodeAnalysis.CSharp` analyzer package consumed by the Avalonia app project during compilation.

## What's in it

| File | Purpose |
|------|---------|
| `ResourceKeyGenerator.cs` | `IIncrementalGenerator` that scans `.axaml` files for `x:Key` / `Key` attributes and emits `Harbor.App.Avalonia.Generated.R` constants. |
| `ReactiveHookGenerator.cs` | `IIncrementalGenerator` that emits `On<Name>Changed` partial-method implementations for `[Reactive]`-annotated `[ObservableProperty]` fields in classes deriving `ReactiveViewModel` — repaint/layout from the attribute kind, plus the `[Reactive(NonNegative = true)]` / `[Reactive(NonNull = true)]` coerce branch (issue #1542). |

## Public API summary

- `ResourceKeyGenerator : IIncrementalGenerator` — registered via `<RoslynComponent>Harbor.CodeGen</RoslynComponent>` in consuming projects.
- `ReactiveHookGenerator : IIncrementalGenerator` — the hook implementation is matched by attribute/base metadata NAME (no project reference needed); attribute lives in `Harbor.Terminal.Abstractions/ViewModels/ReactiveViewModel.cs`. A manual `On<Name>Changed` for a `[Reactive]` field collides with the emitted one and stops the build by design (issue #1542).
- Output namespace: `Harbor.App.Avalonia.Generated`
- Output type: `public static class R` with `internal const string` fields for each distinct key.

## Dependencies

| Package | Purpose |
|---------|---------|
| `Microsoft.CodeAnalysis.CSharp` (5.9.0, per `Directory.Packages.props`) | Roslyn analyzer APIs (`IIncrementalGenerator`, `IncrementalGeneratorInitializationContext`). PrivateAssets=all. The pin matches the compiler the build runs on (SDK 10.0.402 ships Roslyn 5.9.0) — see #1897. |

## Tests

No dedicated test project. Covered indirectly: `ReactiveHookGenerator` by `tests/Harbor.Terminal.Abstractions.Tests/ReactiveViewModelTests.cs` (the stub's generated hooks end-to-end) and the builtin view models by `tests/Harbor.Tui.Tests` / CellForge render tests; the others by the Avalonia app build and UI tests.

## Build

```bash
dotnet build src/Harbor.CodeGen/Harbor.CodeGen.csproj
```

## Known limitations

- Only processes `.axaml` files under `Themes/Hds/`; ignores other XAML dialects and non-AXIAM resource dictionaries.
- Silently swallows malformed XML (`catch {}`) — bad files are skipped without warning.
- Generated constants are `internal`, so only the Avalonia assembly can consume them.
