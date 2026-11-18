# Harbor.Tui.CellForge.CodeGen

Build-time Roslyn component for the CellForge markup DSL (issue #2424). It sits outside the runtime layer matrix: the compiler loads it, it emits code into a consumer's compilation, and it ships no runnable assembly. Slice 0 establishes the project only.

## Public API

`MarkupGenerator` is the single public type. It is a `[Generator]` implementing `IIncrementalGenerator` with one member:

```csharp
public void Initialize(IncrementalGeneratorInitializationContext context)
```

`Initialize` registers one pipeline stage: `AdditionalTextsProvider` selects `.harbor-ui` files, joins them with `AnalyzerConfigOptionsProvider`, and reads the base64 `build_metadata.AdditionalFiles.TargetPath` option (project-relative, stable across checkouts) as the cache key. It emits a small probe source so the wiring is observable. Parser, IR and `BuildRoot` emission are #2426/#2428.

## Wiring

`tests/Harbor.Tui.CellForge.Tests` references this project as an analyzer and declares `Fixtures/Sample.harbor-ui` as an `AdditionalFiles` item with `<TargetPath>`. The generator therefore runs over that sample during the build; `MarkupGeneratorAdditionalTextTests` drives the same generator in-process and checks that the output is keyed by `TargetPath`, not by the absolute checkout path.

## Usage

The type compiles against Roslyn's incremental generator API and is driven with `CSharpGeneratorDriver` by the test suite, over an `AdditionalText` carrying a `build_metadata.AdditionalFiles.TargetPath` option. Directly calling `Initialize` with a default context registers the pipeline but runs nothing.

## Dependencies

- `Microsoft.CodeAnalysis.CSharp` (private) - the compiler API the generator is compiled against.
- `Harbor.Abstractions.Contracts` - home of the DSL IR contracts. The #2425 slice binds no type from it; the reference is recorded in `tools/reference-trimmer-baseline.txt` until the IR slices land.
- No `Harbor.CodeGen` reference: that generator emits string-named attributes for a different domain.
- No `Harbor.Tui.CellForge` reference: a generator binds neither runtime widgets nor their renderers.
- Referenced by `tests/Harbor.Tui.CellForge.Tests` as an analyzer.

## Known limitations

- The emitted source is a wiring probe only. Parser, IR and `BuildRoot` emission are #2426/#2428.
- The contracts reference is unused until the IR slices bind it.
- Not part of the runtime layer matrix and not consumed through DI.
