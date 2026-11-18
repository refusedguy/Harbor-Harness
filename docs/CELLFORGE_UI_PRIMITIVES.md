# CellForge UI primitives catalog: two-pane explorer (P02)

> Documentation slice from the self-contained report in issue #2706. It
> proposes one domain-free primitive and records why it is still open. It does
> not claim the primitive exists in the tree today.

## Why

The CellForge generic layer already ships single-widget primitives: the fuzzy
filter list in
[`FilterListView.cs`](../src/Harbor.Tui.CellForge/Generic/Widgets/FilterListView.cs),
the sortable table in
[`SortableTable.cs`](../src/Harbor.Tui.CellForge/Generic/Widgets/SortableTable.cs),
and the tree in
[`GenericTreeView.cs`](../src/Harbor.Tui.CellForge/Generic/Widgets/GenericTreeView.cs).
Applications compose those widgets into a second, larger pattern: two lists
side by side, one active at a time, with an explicit action that moves a
selection from one side to the other. ranger, broot, lazydocker and
taskwarrior-tui all do this. The user never returns to a command line to move
an item.

In this tree the only two-pane examples are the `two-pane-stack` and
`two-pane-side` gallery seeds in
[`GenericGallery.cs`](../src/Harbor.Tui.CellForge/Generic/GenericGallery.cs).
They prove the layout composes. They paint two static panes, share no focus,
and expose no transfer operation. The primitive is the missing layer between
those seeds and an application.

## Catalog position

Issue #2706 lists 15 primitives. The state column below is a map of the tree
at commit `b1239b7b`, not a claim about future work.

| Id | Primitive | State |
|---|---|---|
| P01 | Fuzzy filter list with match highlight and preview | widget shipped |
| P02 | Two-pane explorer with cross-pane actions | open, this slice |
| P03 | Sortable table with sticky header | widget shipped |
| P04 | Time-series graph or sparkline | widget shipped as CellChart |
| P05 | Navigable diff with change blocks | state primitive shipped |
| P06 | Key legend and status line | engine and overlay support shipped |
| P07 | Modal command palette with history | view shipped |
| P08 | Copy mode with visible selection and OSC52 | widget shipped |
| P09 | Resizable split panes with focus and fullscreen | engine shipped |
| P10 | Log view with follow, filter and wrap | widget shipped |
| P11 | Breadcrumbs with back and forward | engine widget shipped |
| P12 | Collapsible tree with aggregated hidden nodes | widget shipped |
| P13 | Faceted history with session resume | open |
| P14 | Document renderer with pager keys and wrap | view shipped |
| P15 | Inline image with capability negotiation | engine support shipped |

Three items were open when this catalog was written: P02, P05 and P13. P02 was
implemented in #2884 as `TwoPaneState`; P05 is implemented here as
`NavigableChangeBlocks`; P13 covers faceted history.

## The primitive

### Sources

ranger, broot, joshuto, lazydocker and taskwarrior-tui. The report records
these as the applications where two collections share one focus and a transfer
action.

### User benefit

The user sees the source and the destination at the same time. Moving a
selection is one key press. Focus stays in the list, so there is no context
switch to a command prompt and no need to remember a path or identifier.

### Why it generalizes

The primitive knows two collections, which one is active, and one callback
that transfers a selection. It does not know what a collection contains. A
file manager supplies a directory and a preview path. A container tool
supplies a list of containers and a detail pane. A task tool supplies a list
of tasks and a change queue. The callback belongs to the caller.

The data shape is deliberately small: an item identity, a display string and
an optional detail string. Any richer payload stays in the caller and is
looked up by identity when the transfer fires.

### Complexity

Medium. The layout and the focus stack already exist. The work is to define
one state record, keep the two selections consistent, and route the transfer
key through the existing input path.

### Starting point in CellForge

The engine already owns the split geometry in
[`SplitPane.cs`](../src/Harbor.Tui.CellForge.Engine/Rendering/SplitPane.cs)
and the flexible layout in
[`FlexLayout.cs`](../src/Harbor.Tui.CellForge.Engine/Rendering/FlexLayout.cs).
Focus routing lives in
[`FocusRouter.cs`](../src/Harbor.Tui.CellForge.Engine/Input/FocusRouter.cs).
The composition rules are written down in
[`GENERIC_LAYOUT.md`](./GENERIC_LAYOUT.md), and the chat-owned solver in
[`LayoutTree.cs`](../src/Harbor.Tui.CellForge/Chat/Rendering/LayoutTree.cs)
stays out of the primitive. The generic widget catalog is in
[`COMPONENT_CATALOG.md`](./COMPONENT_CATALOG.md). What is missing is the
two-collection state and the transfer callback.

## Proposed contract

The sketch below is a design target, not shipped API. A follow-up
implementation PR can change names, but it should keep the three facts: two
collections, one active pane, one caller-owned transfer.

```csharp
public enum PaneId : byte { Source, Target }

public readonly record struct PaneItem(string Id, string Label, string? Detail);

public sealed class TwoPaneState
{
    public IReadOnlyList<PaneItem> Source { get; }
    public IReadOnlyList<PaneItem> Target { get; }
    public PaneId Active { get; }
    public int SourceCursor { get; }
    public int TargetCursor { get; }
}
```

The widget owns the two cursors and the active pane. The caller owns the
collections and the transfer function:

```csharp
public delegate void TransferSelection(
    PaneId from, PaneId to, PaneItem item);
```

The transfer never mutates `TwoPaneState`. It reports a fact to the caller,
which rebuilds the two collections and hands them back on the next frame.
That keeps the state pure and fits the measure, arrange, paint sequence in
[`GENERIC_LAYOUT.md`](./GENERIC_LAYOUT.md).

### Input model

Left and Right move the cursor inside the active pane. Tab switches the active
pane through the existing focus router. Enter calls the transfer callback with
the item under the active cursor. A modifier on Enter transfers in the
opposite direction, so the same widget serves both directions without a
second binding table. The engine binds keys through
[`KeyLegend.cs`](../src/Harbor.Tui.CellForge.Engine/Input/KeyLegend.cs), so the
hint line comes from the same registry as every other key.

### Paint model

Each pane paints its head, its visible rows and its footer. The active pane
uses the focus style; the other pane uses the dim style. The clip rect comes
from the layout, so a narrow terminal shrinks both panes without changing the
state. Widening a pane is the job of the already shipped split primitive, not
of this one.

## Composition recipe

1. Build two leaf nodes, one per collection, using the pure measure, arrange,
   paint phases from [`GENERIC_LAYOUT.md`](./GENERIC_LAYOUT.md).
2. Wrap them in a horizontal `SplitPane`, giving each side a share of the
   width.
3. Register both leaves with the focus router and map Tab to the switch
   action.
4. Feed `TwoPaneState` from the caller on each frame. Do not cache payloads
   inside the widget.
5. Call `TransferSelection` on Enter. Let the caller push the new collections
   back through its own state store.
6. Paint a key hint from the key legend so the transfer key is discoverable.

## Out of scope

These belong to the application and must not enter the primitive: file paths
and file system access, container identifiers, task schemas, git staging,
sort criteria, and persistence of pane sizes. The primitive receives prepared
items and reports a transfer. It never asks where the items came from.

## Acceptance criteria for the follow-up implementation

A pull request that implements P02 should satisfy all of the following:

- A `TwoPaneState` value produces a deterministic frame for a given clip rect.
- Moving the cursor in one pane does not change the other pane's cursor.
- Tab changes the active pane without moving either cursor.
- Enter calls the transfer callback once with the active pane, the target
  pane and the selected item.
- The reverse transfer uses the same widget and a documented modifier, with no
  second key registry.
- No application domain type appears in the primitive's public surface.
- The two-pane gallery seeds keep painting, so the change does not break the
  existing golden frames.

## P05: navigable change blocks (shipped)

P05 is implemented as
[`NavigableChangeBlocks.cs`](../src/Harbor.Tui.CellForge.Engine/Rendering/NavigableChangeBlocks.cs).
The primitive never names a hunk, a stage or a diff: the caller supplies
prepared `ChangeBlock` records (identity, label and numbered lines), and the
state owns only two sides, one active side, a block index, a line cursor and an
optional selection anchor per side. Moving or selecting on one side never
changes the other. `SelectedRange` is a normalized half-open range, so the
caller gets either the single cursor line or an anchored range. `Rows()` is a
pure projection for a clip and `Paint()` writes both sides to a
`ScreenBuffer`; a transfer is reported as a `BlockTransfer` value, so no git or
application type enters the primitive's public surface.

## Related documents

- [Component catalog](./COMPONENT_CATALOG.md) for the existing widget inventory.
- [Generic layout composition](./GENERIC_LAYOUT.md) for the measure, arrange,
  paint contract.
- [CellForge deep dive](./SPECTRE_TUI_DEEP_DIVE.md) for the render loop and
  hot-path rules.
- [Architecture](./ARCHITECTURE.md) for the layer boundaries the primitive must
  respect.
