# Harbor.Tui.CellForge.CodeGen

Build-time Roslyn component for the CellForge markup DSL (issue #2424). It sits outside the runtime layer matrix: the compiler loads it, it emits code into a consumer's compilation, and it ships no runnable assembly. Slice 0 establishes the project only.

## Public API

`MarkupGenerator` is the single public type. It is a `[Generator]` implementing `IIncrementalGenerator` with one member:

```csharp
public void Initialize(IncrementalGeneratorInitializationContext context)
```

`Initialize` registers no pipeline stage yet, so the generator produces no source.

## Wiring

None. No project references this generator yet, and no environment variable or renderer id activates it. Add it as an analyzer reference when the DSL consumer lands; that wiring belongs to a later issue.

## Usage

The type compiles against Roslyn's incremental generator API:

```csharp
using Harbor.Tui.CellForge.CodeGen;
using Microsoft.CodeAnalysis;

var generator = new MarkupGenerator();
generator.Initialize(default(IncrementalGeneratorInitializationContext));
```

## Dependencies

- `Microsoft.CodeAnalysis.CSharp` (private) - the compiler API the generator is compiled against.
- `Harbor.Abstractions.Contracts` - home of the DSL IR contracts. The slice-0 stub binds no type from it; the reference is recorded in `tools/reference-trimmer-baseline.txt` until the IR slices land.
- No `Harbor.CodeGen` reference: that generator emits string-named attributes for a different domain.
- No `Harbor.Tui.CellForge` reference: a generator binds neither runtime widgets nor their renderers.
- Referenced by: no project yet.

## Known limitations

- The generator emits nothing. Parser, IR and `BuildRoot` emission are separate issues.
- The contracts reference is unused until the IR slices bind it.
- Not part of the runtime layer matrix and not consumed through DI.
