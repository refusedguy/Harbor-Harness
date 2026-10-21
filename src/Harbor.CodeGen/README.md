# Harbor.CodeGen

Internal Roslyn source generator for Harbor. Emits `R` constant classes from AXIAM resource keys found in `Themes/Hds/*.axaml` files. Packaged as a Roslyn component (`IsRoslynComponentPackage=true`), not a runtime library.

## Layer

**Build-time tooling.** No runtime dependency; ships only as a `Microsoft.CodeAnalysis.CSharp` analyzer package consumed by the Avalonia app project during compilation.

## What's in it

| File | Purpose |
|------|---------|
| `ResourceKeyGenerator.cs` | `IIncrementalGenerator` that scans `.axaml` files for `x:Key` / `Key` attributes and emits `Harbor.App.Avalonia.Generated.R` constants. |
| `ReactiveHookGenerator.cs` | `IIncrementalGenerator` that emits `On<Name>Changed` partial-method implementations for `[Reactive]`-annotated `[ObservableProperty]` fields in classes deriving `ReactiveViewModel` — repaint/layout from the attribute kind, plus the `[Reactive(NonNegative = true)]` / `[Reactive(NonNull = true)]` coerce branch (issue #1542). |
| `ToolGlueGenerator.cs` | `IIncrementalGenerator` that emits the scalar `ITool` glue (name, display name, glyph, description, execution mode, prompt snippet and guidelines) of every tool carrying `[ToolInfo]`. |
| `ToolSchemaGenerator.cs` | `IIncrementalGenerator` that renders the JSON Schema of a tool from its `[ToolParam]` declaration and emits the tool's `ParameterSchema` (issue #1780). The rendered schema is hashed and compared with the pin in `src/Harbor.Tools.Builtin/Schemas/tool-schemas.sha256.json`: a schema that differs from its pin is a build error, because the schema is the tool's contract with the model. |
| `ObservedGenerator.cs` | `IIncrementalGenerator` that reads the `[Observed]` marker (issue #1797) and emits a plain C# decorator: a sealed class that implements the observed interface, forwards every member, and opens a span and records the call's metrics around the observed ones. |
| `ObservedDiagnostics.cs` | The diagnostics `ObservedGenerator` reports: two errors (a statement that contradicts itself, two policies that disagree) and three warnings (a member that is forwarded without instrumentation, a class seam this slice does not cover, a name collision). |

## Public API summary

- `ResourceKeyGenerator : IIncrementalGenerator` — registered via `<RoslynComponent>Harbor.CodeGen</RoslynComponent>` in consuming projects.
- `ReactiveHookGenerator : IIncrementalGenerator` — the hook implementation is matched by attribute/base metadata NAME (no project reference needed); attribute lives in `Harbor.Terminal.Abstractions/ViewModels/ReactiveViewModel.cs`. A manual `On<Name>Changed` for a `[Reactive]` field collides with the emitted one and stops the build by design (issue #1542).
- `ToolGlueGenerator : IIncrementalGenerator` — the scalar `ITool` glue is matched by the metadata name `ToolInfoAttribute` (no project reference needed); the attribute lives in `Harbor.Abstractions/Tools/ToolInfoAttribute.cs`.
- `ToolSchemaGenerator : IIncrementalGenerator` — the parameter schema of a tool is matched by the metadata names `ToolSchemaAttribute` and `ToolParamAttribute` (no project reference needed); the attributes live in `src/Harbor.Tools.Builtin/`. The pin it compares against is an `AdditionalFiles` input of the consuming project.
- `ObservedGenerator : IIncrementalGenerator` — the marker is matched by metadata name (`Harbor.Abstractions.Contracts.ObservedAttribute`), so this project needs no reference to the contracts assembly. The emitted decorator depends only on `Harbor.Diagnostics.Abstractions` (`ITracer`, `IMetrics`), so it can live in any assembly that reaches that contract. `HarborWithObservedInstrumentation=false` makes it emit NOTHING.
- Output namespace: `Harbor.App.Avalonia.Generated`
- Output type: `public static class R` with `internal const string` fields for each distinct key.

## Dependencies

| Package | Purpose |
|---------|---------|
| `Microsoft.CodeAnalysis.CSharp` (5.9.0, per `Directory.Packages.props`) | Roslyn analyzer APIs (`IIncrementalGenerator`, `IncrementalGeneratorInitializationContext`). PrivateAssets=all. The pin matches the compiler the build runs on (SDK 10.0.402 ships Roslyn 5.9.0) — see #1897. |

## Tests

`ObservedGenerator` has its own guard: `tests/Harbor.CodeGen.Tests` drives it in-process over a synthetic compilation (does it emit, does it stay silent for an unmarked seam, does a contradictory marker break the build) and runs the decorator it really emitted against a plain `ActivityListener` and `MeterListener`. The seams it runs over live in `tests/Harbor.Telemetry.Fixtures`.

The others have no dedicated test project. Covered indirectly: `ReactiveHookGenerator` by `tests/Harbor.Terminal.Abstractions.Tests/ReactiveViewModelTests.cs` (the stub's generated hooks end-to-end) and the builtin view models by `tests/Harbor.Tui.Tests` / CellForge render tests; the others by the Avalonia app build and UI tests.

`ToolSchemaGenerator` is covered from both sides: the compile-time half is the generator itself (a schema that differs from its pin is a build error, `HTOOL001`), and the runtime half is `tests/Harbor.Tools.Builtin.Tests/ParameterSchemaPinTests.cs`, which checks that what the runtime hands out hashes to the value that was pinned. The python control for the compile-time half is `tools/check-tool-schema-pin.py`; its `--selftest` plants a divergence and requires the analysis to find it.

## Build

```bash
dotnet build src/Harbor.CodeGen/Harbor.CodeGen.csproj
```

## Known limitations

- Only processes `.axaml` files under `Themes/Hds/`; ignores other XAML dialects and non-AXIAM resource dictionaries.
- Silently swallows malformed XML (`catch {}`) — bad files are skipped without warning.
- Generated constants are `internal`, so only the Avalonia assembly can consume them.
- The emitted `[Observed]` decorator decorates INTERFACES only. A class that carries the marker gets a warning, not a decorator: a class seam would need virtual members to be worth wrapping, and the generator refuses to guess.
