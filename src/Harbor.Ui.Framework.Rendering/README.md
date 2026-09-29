# Harbor.Ui.Framework.Rendering

The renderer-agnostic shared UI layer: cell and screen primitives, the key
vocabulary, the cell-diff wire protocol, streaming markdown, and the reusable
chat widgets. BCL-only and AOT-compatible (`<IsAotCompatible>` — zero IL2xxx
warnings).

"Renderer-agnostic" is the load-bearing word. Nothing here draws to a console,
an Avalonia canvas or a browser. It produces **cells, diffs and widget models**;
a backend decides how to paint them. That is what lets the same widgets serve
CellForge, the Avalonia desktop app and the Blazor surface.

## What it is

A **Presentation-layer leaf** in the UI framework family: BCL-only,
AOT-compatible, and renderer-agnostic. It produces cells, diffs and widget
models; a backend decides how to paint them. That is what lets one widget set
serve CellForge, the Avalonia desktop app and the Blazor surface.

## Public API

### Cells and screen

- `Cell` ([`Cell.cs:13`](./Cell.cs)) — `readonly struct : IEquatable<Cell>`.
  A rune plus packed `Fg`/`Bg` (24-bit), `Flags`, and a `Width` of
  `Narrow`/`Wide`/`WSkip` (the invisible tail half of a wide rune, so CJK and
  emoji do not smear). `Cell.Blank`, `Cell.WideTail`, `Cell.FromRaw(...)`.
- `CellStyle` — the style half of a cell.
- `ScreenBuffer` ([`ScreenBuffer.cs:17`](./ScreenBuffer.cs)) — the `Cell[]`
  grid with a per-row hash for diffing. `At(x, y)` returns `ref Cell` for
  zero-copy writes; `Get`, `SetRune`, `Fill`/`FillAll`, `BlankAll`, `Resize`,
  `MarkRowDirty`/`InvalidateAll`. `RowHash` is exposed for the encoder.
- `Rect`, `TextWrap`, `UnicodeWidth` — geometry and text measurement.

### Key vocabulary

`Input/` holds the BCL-only key model that keeps this project a leaf:
`KeyEvent`, `KeyCode`, `KeyEventType`, `KeyModifiers`, and the
`UiKeyDto`/`UiKeyKind`/`UiKeyMods` record struct with `IKeyVocabulary` and
`DefaultKeyVocabulary` ([`IKeyVocabulary.cs`](./Input/IKeyVocabulary.cs)).
`KeyEventMapper` maps a decoded `KeyEvent` onto that vocabulary and
`ConsoleKeyMapper.FromConsoleKeyInfo(ConsoleKeyInfo)` maps a BCL
`Console.ReadKey` press — the one `ConsoleKey` switch in the repository, shared
by the RazorConsole / Termina / TerminalGui shells (#554). `IFocusTarget` is
the focus-stack contract.

### Cell-diff protocol

`Protocol/` defines the wire format a backend can consume:
`ICellDiffEncoder`/`ICellDiffDecoder`/`ICellDiffSink`,
`CellDiffBatch`, `CellDiffBatchCodec`, `CellDiffHints`, `CellDiffMessage` and
`CellDiffProtocolVersion`. `RowHashDiffEncoder` is the production encoder:

```csharp
public CellDiffBatch Encode(
    ScreenBuffer prev,
    ScreenBuffer next,
    IReadOnlyList<Rect>? hints,
    long sequence);
```

It compares per-row hashes, so an unchanged frame costs one pass and no
allocation.

### Streaming markdown

`StreamingMarkdownRenderer` ([`Markdown/`](./Markdown/)) pushes text chunks in
(`Push`, `Complete`) and hands back finished lines (`GetLines`, `LineCount`,
`FrozenLineCount`, `Checkpoint`, `IsComplete`, `Width`). The frozen/tail split
is what makes re-rendering a long stream cheap. `MarkdownBlockParser`,
`DifferentialMarkdownPipeline` and `FrozenTailMarkdownCache` build on it.

### Widgets

`Widgets/` holds the reusable chat models, all implementing `IChatBlock` (or
`ICollapsibleChatBlock` where collapsing applies):

- `StatusViewModel` with `AgentPhase`, `MascotReaction`, `StatusAccent`,
  `StatusSeg`, `StatusBarMode` and the `StatusBarLayout`/`StatusBarWidget`
  renderers — model, cost, tokens, phase, context-window usage, mascot signal.
- `DiffBlock` with `DiffLineKind`/`DiffLine` and the static `UnifiedDiffParser`;
  `WordDiff` with `WordSegKind`/`WordSeg`/`WordDiffSides` for intra-line diffs.
- `ApprovalGateView` with `ApprovalChoice`; `QuestionFormView` with
  `QuestionItem`/`QuestionOption`/`QuestionAnswer`.
- `CostBarsBlock` with `CostBar`; `SparklineBlock`; `Tabs`; `TreeView` with
  `TreeNode`/`TreeRow`.
- `UserBlock`/`SystemBlock` (the basic text blocks) with `WrappedText`,
  `BlockMeasure` and `BlockPaintContext`; `PanelFx`.

### Pipelines and performance contracts

`DifferentialRenderPipeline` and `DifferentialMarkdownPipeline` are the
frame/markdown pipelines; `FrameTicker` and `AnimationClock` drive them.
`PerformanceContracts/` holds `RendererPerformanceContract` and
`MarkdownRenderPerformanceContract` — the allocation budgets the renderers are
held to.

## Wiring

**No DI module and no `HARBOR_*` env var.** This project registers nothing and
reads no configuration; it is consumed by direct construction from the backends.
It is present in a full build because `src/Harbor.Hosting` and
`apps/Harbor.App.Cli` reference the CellForge stack above it.

Layout (from issue #33 T1): this is a **leaf** over `Harbor.DesignSystem` and
`Harbor.Desktop.Animations`. `State` consumes it via `KeyEventAdapter`; the
`Rendering → Projection → State` chain that would have been circular was never
realised and was removed.

The assembly exposes `InternalsVisibleTo` to `Harbor.Tui.CellForge`,
`Harbor.Tui.CellForge.Engine` and `Harbor.Tui.CellForge.Tests` — the CellForge
class renderers consume the zero-alloc fast paths (`ScreenBuffer` row-hash
internals, `BlockMath`, `StreamingMarkdownRenderer.RenderRange`) across the
assembly boundary **by design**.

## Usage

Filling a screen and encoding the change since the last frame:

```csharp
using System.Text;
using Harbor.Ui.Framework.Rendering;
using Harbor.Ui.Framework.Rendering.Protocol;

var buffer = new ScreenBuffer(cols: 120, rows: 40);
buffer.Fill(new Rect(0, 0, 120, 40), Cell.Blank);
buffer.SetRune(0, 0, new Rune('>'), default);

CellDiffBatch batch = new RowHashDiffEncoder().Encode(prev, buffer, hints: null, sequence: 1);
```

A chat widget, e.g. an approval gate:

```csharp
var gate = new ApprovalGateView(
    toolName: "bash",
    detail: "rm -rf build/",
    invocationId: "tc_1",
    generation: 1);
```

Streaming markdown. The renderer is parameterless; width is supplied per
`RenderTail` call, and a width change invalidates the frozen geometry:

```csharp
using Harbor.Ui.Framework.Rendering.Markdown;

var md = new StreamingMarkdownRenderer();
md.Push("# Title\nsome tex");
md.Push("t…");
md.RenderTail(width: 80);
md.Complete();

IReadOnlyList<MdLine> lines = md.GetLines();
```

## Dependencies

| Reference | Why |
|-----------|-----|
| `Harbor.Abstractions.Contracts` | the canonical `ContextUsage` helper consumed by `StatusViewModel` |
| `Harbor.DesignSystem` | colour/style tokens |
| `Harbor.Desktop.Animations` | animation primitives |

Referenced by `Harbor.Ui.Framework.State`, `Harbor.Tui.CellForge`,
`Harbor.Tui.CellForge.Engine`, and the UI test/benchmark projects. Per CF-A-001
(precedent #75) these are **direct** references, not transitive ones; the edge
set is pinned by `tests/Harbor.Architecture.Tests`.

## Known limitations

- **Not a renderer.** There is no `ITuiRenderer`, no console output and no
  platform interop here. Painting is the backend's job.
- **No `State`/`Projection` edge, by design.** Consumers wanting the widget
  *state* models need `Harbor.Ui.Framework.State`; this project carries only the
  drawing primitives. The old `Rendering → Projection` reference was removed as
  unrealised.
- **`InternalsVisibleTo` is a real coupling.** The zero-alloc paths are consumed
  from CellForge via internals, so a rename inside this assembly is not
  automatically a compile error at the call site.
- **`UnicodeWidth` and `TextWrap` follow a fixed-width terminal model.** Cells
  have a single `Width` byte; grapheme-cluster and emoji-ZWJ sequences that need
  more than two columns are not representable.
- **`UnicodeWidth.WidthCached` memoizes per thread, not per process.** The table
  is `[ThreadStatic]` (256 slots, ~4 KB) so a measurement never takes a
  process-global monitor — the in-process renderer, the IPC client and a plugin
  renderer can measure text at the same time. The trade is one rune decode per
  distinct run *per thread* instead of per process, and 4 KB retained by every
  thread that ever measures text; both are start-up-scale, not frame-scale.
  `StatusBarLayout.Fit` (#487) measures each segment exactly once per call, so
  the per-thread cache sees a flat one-lookup-per-segment cost.
- `IsAotCompatible` means no reflection-based JSON and no
  `JsonSerializer` without a source-generated context.
- `PerformanceContracts/` are budgets, not enforcement: they are asserted by
  benchmarks and perf tests rather than at runtime.

## See also

- [`../Harbor.Ui.Framework.State/README.md`](../Harbor.Ui.Framework.State/README.md) — the state layer that consumes this project's key vocabulary
- [`../Harbor.Tui.CellForge.Engine/README.md`](../Harbor.Tui.CellForge.Engine/README.md) — the terminal backend built on these primitives
- [`../Harbor.Tui.CellForge/README.md`](../Harbor.Tui.CellForge/README.md) — the canonical cell-diff renderer
- [`../../docs/ARCHITECTURE_LAYERS.md`](../../docs/ARCHITECTURE_LAYERS.md) — layering rules
