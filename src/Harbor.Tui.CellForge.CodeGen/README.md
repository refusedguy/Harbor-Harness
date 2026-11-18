# Harbor.Tui.CellForge.CodeGen

Build-time Roslyn component for the CellForge markup DSL (issue #2424). It sits outside the runtime layer matrix: the compiler loads it, it emits code into a consumer's compilation, and it ships no runnable assembly.

## Public API

- `MarkupGenerator` is the `[Generator]` entry point with one member, `Initialize(IncrementalGeneratorInitializationContext)`.
- `MarkupParser.Parse(string)` reads `.harbor-ui` text into a `MarkupParseResult`. The result carries a `SpecArena` and a list of `MarkupDiagnostic` values with line and column.
- `MarkupParseResult` and `MarkupDiagnostic` are the value types returned by the parser.

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
- Arguments are quoted strings, numbers, `true`/`false` or bare names. The parser classifies each one as a `SpecValueKind`.
- Syntax errors become `MarkupDiagnostic` values with a 1-based line and column. The generator forwards them as `HUI001` compiler diagnostics.

The kind list covers the current generic widgets: `Panel`, `Text`, `FilterList`, `SortableTable`, `Tree`, `LogView`, `TextInput`, `DatePicker`, `Table`, `SelectionList`, `TextField`, `NumberInput`, `MultilineTextInput`, `Accordion`, `ActionButton`, `AnchoredMenu`, `AnchoredOverlay`, `AttachmentCard`, `CompactLabel`, `CopyMode`, `DocumentView`, `Dropdown`, `FocusTooltip`, `Form`, `FormScaffold`, `LiveRegion`, `Progress`, `Questionnaire`, `Rating`, `SettingsPanel`, `Spinner`, `Stepper`, `ToggleSelect`, `Toolbar` and `VirtualList`.

Every node is single-use. `SpecNode.AddChild` rejects a child that already has a parent, and `SpecNode.Claim` rejects a second consumer. Both throw `SpecNodeReuseException` naming the kind and source line instead of leaving two owners pointing at one node.

## Wiring

`tests/Harbor.Tui.CellForge.Tests` references this project as an analyzer and declares `Fixtures/Sample.harbor-ui` as an `AdditionalFiles` item with `<TargetPath>`. `MarkupGeneratorAdditionalTextTests` drives the same generator in-process and checks that the output is keyed by `TargetPath`, not by the absolute checkout path.

## Usage

The generator calls `MarkupParser.Parse` before emitting a source. The dev interpreter calls the same method over the same file text; no second grammar or parser exists. `BuildRoot` emission is #2428, so the emitted source is still a wiring probe.

The parser depends only on the BCL and `Harbor.Abstractions.Contracts`. It holds no Roslyn, CellForge runtime or renderer state, which keeps it usable from an AOT-safe generator and from a dev interpreter.

## Dependencies

- `Microsoft.CodeAnalysis.CSharp` (private) - the compiler API the generator is compiled against.
- `Harbor.Abstractions.Contracts` - the DSL IR the parser builds and the generator reads.
- No `Harbor.CodeGen` reference: that generator emits string-named attributes for a different domain.
- No `Harbor.Tui.CellForge` reference: a generator binds neither runtime widgets nor their renderers.
- Referenced by `tests/Harbor.Tui.CellForge.Tests` as an analyzer.

## Known limitations

- The emitted source is a wiring probe. `BuildRoot` emission is #2428.
- The parser records operations without binding them to widget methods. Binding belongs to the generator and dev interpreter, both of which read the same IR.
- Not part of the runtime layer matrix and not consumed through DI.
