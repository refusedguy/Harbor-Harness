# Generic layout composition on CellForge

Contract doc for composing ARBITRARY TUI interfaces on the CellForge engine
(epic #1195). The composition layer lives inside the existing assemblies —
no new extension axes (feature freeze #555) — and never touches the chat
layer (`Chat/Rendering/LayoutTree.cs` stays the chat-owned collapse-priority
solver; its binary split-ratio math is a read-only sample here).

Source discipline: Termina `BuildLayout`-no-mutations plus the
`IPanelProvider` purity requirement (state → rows, render/input threads).

## 1. Node contract

Every composable node implements three phases, in this order, on the render
thread only:

1. `Measure(Constraints) -> Size` — report the desired size within the given
   bounds. Pure: no buffer writes, no state mutations.
2. `Arrange(Rect)` — accept the final rect assigned by the parent. Stores the
   rect, positions children via the pure solvers (`FlexLayout`,
   `StackLayout`, `CenterLayout`). No painting.
3. `Paint(ScreenBuffer, Rect)` — paint within the arranged rect clipped to the
   given clip. Reads state, writes cells, mutates nothing else.

```csharp
Constraints avail = new(MaxWidth: 80, MaxHeight: 24);
Size want = node.Measure(avail);   // desired, clamped to avail
node.Arrange(new Rect(0, 0, 80, 24));
node.Paint(buffer, new Rect(0, 0, 80, 24));
```

`Constraints` carries only bounds (max width/height, both clamped at zero).
Unbounded axes do not exist: a child that wants "as much as possible"
declares a flexible track and the parent's arrange phase resolves it.

Leaf and container bases share one vocabulary (`Rect`, `Size`, `SplitDir`,
`ScreenBuffer` — all engine-owned, BCL-only, AOT-compatible):

- `LeafNode` — no children. Default `Arrange` stores the rect; subclasses
  implement `Measure` and `Paint` only.
- `ContainerNode` — owns an ordered child list (`Add`/`Remove` are
  setup-time; the steady-state frame path allocates nothing). Subclasses
  implement the three phases by delegating to the pure solvers.
- `FlexSplitNode` — n-ary Row (`SplitDir.Horizontal`) / Column
  (`SplitDir.Vertical`) over `FlexLayout.Arrange`. One `FlexTrack` per child
  (`Fixed` / `Percent` / `Fill` / `Min` / `Max`); gap and `FlexJustify`
  behave exactly as in `FlexLayout`.
- `ZStackNode` — full-area overlap stack over `StackLayout.Arrange`: every
  child takes the whole rect, painted in index order (lower first).

```csharp
var root = new FlexSplitNode(SplitDir.Vertical);
root.Add(header, FlexTrack.Fixed(3));
root.Add(body, FlexTrack.Fill(1));
root.Add(footer, FlexTrack.Fixed(1));

var overlay = new ZStackNode();
overlay.Add(root);
overlay.Add(dialog);
```

### 1.1. `Constraints` semantics

| Member | Meaning |
|---|---|
| `MaxWidth` / `MaxHeight` | Hard upper bounds for `Measure`. Negative counts as zero. |
| No unbounded axis | There is no "infinite" bound. Flexibility is declared with tracks (`Fill` / `Min` / `Max`) and resolved by the parent's arrange phase, never by measuring into infinity. |
| Desired ≤ bounds | A well-behaved `Measure` returns a `Size` clamped to the bounds. Parents clamp defensively anyway. |

### 1.2. Solver → node mapping

The nodes add no layout math of their own — each one seats an existing pure
solver behind the three-phase contract:

| Solver (exists) | Node (new) | Phase wiring |
|---|---|---|
| `FlexLayout.Arrange` | `FlexSplitNode` | `Arrange` deals one `Rect` per child; `Measure` sums fixed/percent shares plus `Min` floors, clamped to bounds |
| `StackLayout.Arrange` | `ZStackNode` | `Arrange` hands every child the whole rect; `Measure` takes the clamped max of children |
| `CenterLayout.Arrange` | leaf-level, inline | Dialog-style nodes center their content box inside the arranged rect during `Paint` setup, no node needed |

`LayoutTree` (chat) is deliberately absent from this table: its
collapse-priority water-filling solver stays chat-owned. Generic
composition reads it as a sample only.

## 2. Worked example

A header/body/footer column with a centered dialog on top:

```csharp
// Setup (once): build the tree, wire tracks.
var page = new FlexSplitNode(SplitDir.Vertical);
page.Add(new HeaderNode(), FlexTrack.Fixed(3));
page.Add(new BodyNode(), FlexTrack.Fill(1));
page.Add(new StatusNode(), FlexTrack.Fixed(1));

var frame = new ZStackNode();
frame.Add(page);
frame.Add(new DialogNode());

// Steady-state frame (render thread): measure → arrange → paint.
Size want = frame.Measure(new Constraints(viewport.Width, viewport.Height));
frame.Arrange(new Rect(0, 0, viewport.Width, viewport.Height));
frame.Paint(back, new Rect(0, 0, viewport.Width, viewport.Height));
```

`Measure` may be skipped when the viewport geometry is unchanged — the
arranged rects from the previous frame are still valid. `Arrange` must run
before every `Paint` that follows a tree mutation or a resize.

## 3. Forbids

- No mutations in `Measure` or `Build`. Measuring answers "how big"; it never
  writes cells, never resizes buffers, never advances animations.
- No painting in `Arrange`. Arrangement assigns rects; pixels move in `Paint`.
- No chat-layer edits. `LayoutTree`, `Panel`, `SplitNode`, `BorderPanel` are
  chat-owned; generic composition reuses only the engine solvers as samples.
- No new assemblies, no new `ProjectReference`, no new plugin axes (#555).
  New nodes land in `Harbor.Tui.CellForge.Engine/Rendering`, BCL-only.
- No allocations on the steady-state frame path: `for` index loops, reused
  rect scratch (`stackalloc` under 256 children), no LINQ, no closures.
- No thread-safety: like `FlexLayout` and `DiffEngine`, nodes are
  render-thread owned. Cross-thread state arrives as immutable snapshots.
- No negative geometry: negative extents count as zero at every boundary
  (`Constraints`, `Arrange`, `Paint` clip).

## 4. Build purity rule

`IPanelProvider.Build` is the panel-side instance of the same discipline as
`Measure` (Termina `BuildLayout`-no-mutations):

- `Build(ctx)` reads `ctx.State` and returns rows (`string`,
  `IReadOnlyList<string>`, `IEnumerable<string>`). It MUST be side-effect
  free: no `UiStore.Dispatch`, no cache writes, no I/O.
- `OnKey` may mutate provider-local cache but drives state transitions only
  through `UiStore.Dispatch` — never by mutating the `UiState` record.
- The host may call `Build` on the render thread while `OnKey` runs on the
  input thread: implementations MUST be thread-safe (immutable state, no
  unsynchronized shared mutable fields).
- Geometry is the provider's own job: clip with
  `PanelText.Clip(rows, ctx.Width, ctx.Height)`. Anything `Build` returns
  outside the three row shapes falls through to `ToString()` and paints its
  own type name — never return a widget object.

## 5. Non-goals (later TRs)

This doc pins only the measure/arrange/paint contract and the Build purity
rule. Explicitly out of scope here: the widget catalog (forms, inputs,
tables, trees, charts), styling/theming, the focus chain and key-message
routing, and the test harness. Those follow as separate TRs under #1195 —
this contract is the foundation they build on, not a placeholder for them.
