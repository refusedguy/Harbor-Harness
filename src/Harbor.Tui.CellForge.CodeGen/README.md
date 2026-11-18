# Harbor.Tui.CellForge.CodeGen

Build-time Roslyn component for the CellForge markup DSL (issue #2424). It sits outside the runtime layer matrix: the compiler loads it, it emits code into a consumer's compilation, and it ships no runnable assembly.

## Public API

- `MarkupGenerator` is the `[Generator]` entry point with one member, `Initialize(IncrementalGeneratorInitializationContext)`.
- `MarkupParser.Parse(string)` reads `.harbor-ui` text into a `MarkupParseResult`. The result carries a `SpecArena`, an optional `state:` type, and a list of `MarkupDiagnostic` values with line and column.
- `MarkupParseResult` and `MarkupDiagnostic` are the value types returned by the parser.
- `TypedBindingsEmitter` resolves `{Binding ...}` paths against the state type through Roslyn symbols and emits typed delegates.
- The emitted contract, declarations, `BuildRoot` and binding delegates live in the consuming compilation under `Harbor.Tui.CellForge.CodeGen.Generated`; the emitters (`BuildRootEmitter` and `TypedBindingsEmitter`) are internal to this assembly.

`SpecArena`, `SpecNode`, `SpecOp`, `SpecValue`, `SpecKind`, `SpecValueKind`, `SpecTreeDumper` and `SpecNodeReuseException` live in `Harbor.Abstractions.Contracts` under `Harbor.Abstractions.Dsl`.

Every `SpecNode` carries a 1-based `SourceLine` and `SourceColumn`; every `SpecOp` carries the same pair.
The parser fills them from the indentation of the declaration or operation line, and the older two-argument
constructors remain for callers that do not have a source location. `arena.DebugTree()`, `node.DebugTree()` and
`SpecTreeDumper.DebugTree(...)` render the IR as a deterministic LF-terminated text tree (kind, operations,
children, claim state). The dump needs no renderer, so snapshot tests can compare it directly.

## Grammar

The parser reads one declaration per line. Indentation is made of spaces, and a deeper line becomes a child of the previous declaration. A line starting with `#` is a comment. Blank lines are ignored.

```text
# A root node with a title operation and two children.
panel "main":
    .title "Files"
    filter_list "files":
        .height 10
        .multiple true
    text "done"
```

- A node line starts with a kind, followed by optional arguments and an optional trailing `:`. The arguments are recorded as a `new` operation.
- Kind names ignore case, underscores and dashes, so `filter_list`, `filter-list` and `FilterList` resolve to `SpecKind.FilterList`.
- An operation line starts with `.`, then a name and optional arguments. It attaches to the nearest enclosing node.
- `.id "name"` sets `SpecNode.Id`, a stable identity that becomes a named `ElementId` step at runtime. A keyed child keeps its prepaint record and element state when siblings are reordered.
- Arguments are quoted strings, numbers, `true`/`false` or bare names. The parser classifies each one as a `SpecValueKind`.
- Syntax errors become `MarkupDiagnostic` values with a 1-based line and column. The generator forwards them as `HZ0001` compiler errors with a location in the `.harbor-ui` file. A document that has diagnostics produces no generated source, so no empty `BuildRoot` is emitted or cached.
- The generated declarations carry a SHA-256 `SourceChecksum` of the source text so dev mode can reject stale output, plus a comment per node and operation with its 1-based line and column from the IR.

The kind list covers the current generic widgets: `Panel`, `Text`, `FilterList`, `SortableTable`, `Tree`, `LogView`, `TextInput`, `DatePicker`, `Table`, `SelectionList`, `TextField`, `NumberInput`, `MultilineTextInput`, `Accordion`, `ActionButton`, `AnchoredMenu`, `AnchoredOverlay`, `AttachmentCard`, `CompactLabel`, `CopyMode`, `DocumentView`, `Dropdown`, `FocusTooltip`, `Form`, `FormScaffold`, `LiveRegion`, `Progress`, `Questionnaire`, `Rating`, `SettingsPanel`, `Spinner`, `Stepper`, `ToggleSelect`, `Toolbar` and `VirtualList`.

Every node is single-use. `SpecNode.AddChild` rejects a child that already has a parent, and `SpecNode.Claim` rejects a second consumer. Both throw `SpecNodeReuseException` naming the kind and source line instead of leaving two owners pointing at one node.

Issue #2429 adds a `state:` directive and typed bindings. The directive names a C# type visible to the compilation; it is parsed once and is not a widget node.

```text
state: Sample.CounterState
panel "main":
    text "{Binding Count}"
    text_input "{Binding Title, Mode=TwoWay}"
```

A `{Binding Path}` is read-only and emits a `Func<TState, TValue>` whose lambda accesses the property directly. Adding `Mode=TwoWay` also emits an `Action<TState, TValue>` that assigns the final property. Paths may traverse several properties, for example `User.Name`. Every segment must be a public readable property, and a two-way path must end in a property with a public setter.

The generator resolves the path with Roslyn symbols at build time. A missing `state:` annotation (HUI002), an unknown type (HUI003), an unknown or renamed property (HUI004), or a read-only two-way path (HUI005) is an error that breaks the build. HUI004 and HUI005 point at the binding's line and column in the markup file. The emitted delegates use direct property access, so the update path has no reflection and stays AOT-safe.

## Generation phases (#2428 and #2429)

For each `.harbor-ui` file the generator emits four sources:

1. `CellForgeRootContract.g.cs` is added once per compilation during post-initialization. It declares `internal interface ICellForgeRoot` with `Begin(int sequence)`, `Text(int sequence, string text)` and `End()`. The interface types the `root` parameter, so it reaches the compilation before any file is generated.
2. `<Identifier>.Declarations.g.cs` is phase 1. It holds the `internal partial class` and its declarations (`TargetPath`, `RootCount`, `SequenceCount`). Its provider is `MarkupGenerator.Phase1Declarations`.
3. `<Identifier>.BuildRoot.g.cs` is phase 2. It holds the other partial half with `internal static void BuildRoot(ICellForgeRoot root)`. Its provider takes phase 1 as input, so it cannot run before the declarations exist.
4. `<Identifier>.Bindings.g.cs` is phase 3 (#2429). It holds one static class with a `Func<TState, TValue>` field per binding and an `Action<TState, TValue>` field per two-way binding. Its provider combines phase 1 with the compilation, because path resolution needs Roslyn symbols.

`BuildRoot` walks the `SpecArena` in pre-order. Every node emits `root.Begin(seq)` and `root.End()`; a `Text` node also emits `root.Text(seq, literal)`. Sequence numbers are local, dense integers starting at zero. The consumer uses them as cell-diff keys, the role a Blazor render-tree frame sequence plays.

The whole pipeline is keyed by `(TargetPath, text)`, both strings, so an unchanged file reuses the cached step. The output is plain C#: direct calls and literals, with no reflection, no `dynamic` and no emit.

## Wiring

`tests/Harbor.Tui.CellForge.Tests` references this project as an analyzer and declares `Fixtures/Sample.harbor-ui` as an `AdditionalFiles` item with `<TargetPath>`. `MarkupGeneratorAdditionalTextTests` drives the same generator in-process and checks the phase split, the sequence numbers, and that the cache key is `TargetPath` rather than the absolute checkout path. `TypedBindingsGeneratorTests` runs it over a compilation with a sample state type and checks the emitted delegates, the error diagnostics and their markup positions.

## Usage

The generator calls `MarkupParser.Parse` before emitting a source. The dev interpreter calls the same method over the same file text; no second grammar or parser exists.

The parser depends only on the BCL and `Harbor.Abstractions.Contracts`. It holds no Roslyn, CellForge runtime or renderer state, which keeps it usable from an AOT-safe generator and from a dev interpreter.

## Dev interpreter (#2433)

`Harbor.Tui.CellForge.CodeGen.DevReload.DevReloadInterpreter` is the DEV-only re-interpreter. It re-reads a `.harbor-ui` file with `MarkupParser.Parse`, strips full-line comments, and computes a template signature from the emptied IR (kinds, nesting and operation names, with every argument value dropped). The signature drives the Dioxus-style decision:

- Same signature, changed values: lower a patch with the #2439 mutation batch (`MutationQueue` to `MutationBatchApplier`) and apply it to the retained tree. Element ids are the stable pre-order sequences, so a host's per-id view-model, focus and scroll state stays put on unchanged positions.
- Different signature: rebuild the retained tree in place and drop per-id state.
- Changed `state:` directive or binding/handler operation: rebuild and report `RestartRequired`; generated delegates would be stale.
- Parse error: reject the step and leave the live tree untouched.

The interpreter lives in this build-time tooling assembly, not in the runtime. The CLI references this assembly only with `HarborWithDslDev=true`; an AOT publish switches that flag and the `HARBOR_WITH_DSL_DEV` symbol off together, so no dev-interpreter code, JIT, Roslyn compilation or Subsecond engine reaches the AOT binary. The `harbor dsl reload <before> [<after>]` verb drives it interactively, behind the same dev-only symbol.

## Dependencies

- `Microsoft.CodeAnalysis.CSharp` (private) - the compiler API the generator is compiled against.
- `Harbor.Abstractions.Contracts` - the DSL IR the parser builds and the generator reads.
- No `Harbor.CodeGen` reference: that generator emits string-named attributes for a different domain.
- No `Harbor.Tui.CellForge` reference: a generator binds neither runtime widgets nor their renderers.
- Referenced by `tests/Harbor.Tui.CellForge.Tests` as an analyzer.

## Known limitations

- Only `Text` nodes produce `root.Text` calls; other operations are recorded in the IR but not bound to widget methods yet. Binding belongs to the generator and dev interpreter, both of which read the same IR.
- Syntax diagnostics use the `HZ0001` id with a `Location` built from the parser's line and column. Binding diagnostics HUI002 through HUI005 are errors too; HUI004 and HUI005 carry the markup line and column.
- Not part of the runtime layer matrix and not consumed through DI.
