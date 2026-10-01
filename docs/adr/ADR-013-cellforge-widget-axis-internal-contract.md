# ADR-013: the CellForge widget axis is an internal contract — both plugin seams, and the one that is not

> **Status (2026-10-01):** dated record, not normative. Every `file:line` below was
> read at `dev` = `7aff0c87`. This document records a MEASUREMENT and a decision
> that is already implemented; it asserts no machine-checkable fact about the tree
> as it stands, which is why it carries no `Status: normative` banner. The parts
> that must stay true ARE fenced, by two rules in `tools/check-doc-cites.py` and
> seven in `tests/Harbor.Architecture.Tests/CellForgeWidgetAxisRules.cs`.

## Status

Accepted as a record. The question #564 asked is settled, one half of it was
already settled before this issue was filed, and the other half turned out to be a
documentation defect rather than an architectural one.

## Context

#564's title claims three things. Measured on `dev` = `7aff0c87`:

| claim | verdict |
|---|---|
| CellForge widgets are closed at compile time | **true, and always was** |
| the documented `ITuiPlugin`/`ITuiView` seam is collected and never rendered | **true, fixed in #780 + #966** |
| a plugin panel that returns a widget paints its class name | **true, and it is still true** |

The third is the one that survived, and it survived because the INTERFACE
documented the opposite. That is the same defect #916 found in
`src/Harbor.Terminal.Abstractions/README.md:24` and in eight residual prose
sites, one document further out.

## The three axes, and which of them has a door

The word "seam" covers three different shapes here, and #564 measured them
together. They are not the same thing:

| axis | contract | implementations | product reader | verdict |
|---|---|---|---|---|
| **view** | `ITuiPlugin` / `ITuiView` | **0** | **0** | closed, #780 + #966 |
| **panel** | `ITuiPanelPlugin` / `IPanelProvider` | 12 in-tree, **0 plugins** | 4 call sites | **live, text-only** |
| **widget** | `Panel` subclass, spliced by hand into `LayoutTree` | 45 peers | n/a | internal contract |

Two axes that no plugin uses, differing only in whether something reads them.
That is the precise statement of what #564 found, and it is worth writing down
because "two unused axes" reads as an argument for opening both and "one closed,
one open" reads as an argument for neither.

**The panel axis has a real door.** `ITuiPanelPlugin.RegisterPanels` is
dispatched by `PluginRegistrar.Register` (`PluginRegistrar.cs:101`), the
`PanelRegistryPluginAdapter` is a real `IPanelRegistry` facade, and
`CellForgePanelAdapter.RenderToRows` has four product callers in
`ChatScreenLayout.cs` and `CellForgeJumpPaletteOverlayLayer`. A plugin that
registers a panel today gets text on screen.

**The view axis has a marker and a door and no consumer.** `RegisterTuiPlugin`
IS invoked and stores nothing (`PluginLoadHostAdapter.cs:153`); `ITuiPlugin`'s
own XML doc says so; six teaching documents say so; and
`ViewSeam_HasNoFirstImplementorInProductSource` keeps the first
`class Foo : ITuiPlugin` from being written by accident. Closed deliberately.

## Decision

**The widget axis stays an internal contract. This is not a defect.**

`#555` freezes new axes, and #945 already established that landing a panel as a
layer IS a new widget primitive. The cost of an in-tree widget — 4-5 files, all
structural except the widget itself, spliced by hand in `ChatScreen.Build` — is
the correct price, not a smell: the tree-splice is explicit and the `Panel`
contract is one method. There is no `LayoutTree.AddWidget` and no plugin path
into the tree, and adding one is the owner's decision, not this issue's.

**The panel axis stays text-only. Also not a defect — but it was a lie until
this commit.**

`IPanelProvider.Build` returns `object?` so that
`Harbor.Ui.Framework.State` need not reference a TUI framework. That reason is
sound and the signature stays. What was wrong is the sentence that followed it:
"Each concrete renderer casts the returned widget to its native widget type —
SpectreTUI panels return `Spectre.Tui.IWidget`."

* no shipped renderer casts. `CellForgePanelAdapter.WidgetToRows` matches
  `string`, `IReadOnlyList<string>`, `IEnumerable<string>`, and falls through to
  `widget.ToString()` — so a widget object paints its own type name;
* `Spectre.Tui.IWidget` is declared in **0 files**, and SpectreTUI is `contrib/`:
  unmaintained, absent from `Harbor.slnx`, not compiled by CI;
* `AnsiPlain` and `NickConsoleEx` touch no panel type at all (0 files each,
  against 9 in CellForge), so the doc's advice to "ship one
  `IPanelProvider` per renderer assembly" would produce assemblies that are
  collected and never read — the same shape #564/#916 closed one level up.

All 12 in-tree providers return `PanelText.Clip(rows, ctx.Width, ctx.Height)`.
That is the contract, and it is now the documented one.

## What this cost, concretely

Three documents taught the axis and all three lied, in the plugin author's path:

1. `docs/PLUGIN_DEVELOPMENT.md` Example 5 — "TUI panel showing LSP diagnostics",
   declared `: ITuiPanelPlugin`, and implemented `CreatePanel()` +
   `TuiViewBase` + `ITuiViewModel` instead. `RegisterPanels` absent,
   `TuiPanelDescriptor` in 0 files, four usings of the deleted facade. **Five
   compile errors**, and it routed through the closed view seam while claiming
   the live one.
2. `docs/SPECTRE_TUI_DEEP_DIVE.md` §13.5.2(b) — the recipe AGENTS.md sends
   plugin authors to. `Build` returned `new Spectre.Tui.Paragraph()`, which no
   shipped build can render; the prose named a `SpectreTuiRenderer` that is in
   0 files; two dead usings; and it omitted `RequiredHarborVersion` and
   `ShutdownAsync`, which `IPlugin` does not default.
3. `docs/EXAMPLES.md` §19/§20 — three more dead-facade usings.

Plus the file's own **warning banner**, which said the default CLI build "still
compiles it in via the `HarborWithSpectreTui` flag (on by default)". The flag IS
defined and DOES default to `true` (`Harbor.App.Cli.csproj:84`) — but the
`<ItemGroup>` that would reference the contrib renderers is commented out
(`Harbor.App.Cli.csproj:299-309`), so `true` adds no `ProjectReference`. A flag
being defined is not a flag being wired.

## Consequences

* The three documents now teach the contract a shipped build actually reads.
* Two new rules in `tools/check-doc-cites.py` keep them teaching it:
  `DOC-PLUGIN-GUIDE-NS-UNDECLARED` and `DOC-BASE-LIST-UNIMPLEMENTED`, both red
  on the pre-fix tree with a 7-finding log, both floored in `docs.yml`.
* A plugin author who follows the docs now gets compiling code and text on
  screen. Before this change they got six compile errors, or a dock painting
  `Harbor.Spectre.Tui.Paragraph`.
* `ITuiPlugin` is untouched and still closed. Nothing here reopens it.

## Known limits, stated rather than hidden

* `DOC-PLUGIN-GUIDE-NS-UNDECLARED` checks namespaces, not type names. A fence
  naming a `Harbor.*` type that does not exist is still uncaught — ADR-011
  measured the type-name form at 34 foreign names in 62 and refused it, and
  that refusal still holds.
* `DOC-BASE-LIST-UNIMPLEMENTED` is one interface wide. The general form needs a
  signature reader, which ADR-011 established is not available to a stdlib
  script.
* Neither rule compiles anything. They catch the two shapes that are decidable
  from the text; a sample can still be wrong in a way that needs Roslyn, and
  #849's `NotInParallel` arity case is still out of reach by construction.
* The `Harbor.Ui.Framework.Panels` reference set for CS-source plugins is still
  built from an `AppDomain.CurrentDomain.GetAssemblies()` snapshot
  (`PluginAssemblyReferences.cs:89`) with no explicit `EnsureReference` for it.
  A CS-source panel plugin compiles only if that assembly happens to be loaded.
  **Not fixed here** — it is #564's original finding 3, it changes plugin
  compilation rather than documentation, and it wants its own issue.

## Alternatives considered

* **Add a CellForge-native widget case to `WidgetToRows`** (an
  `ITextPanelWidget { void Paint(ScreenBuffer, Rect) }` in
  `Harbor.Tui.CellForge.Engine`, matched by `is`). #564's own recommendation.
  Rejected as an owner decision under `#555`: it is a new widget primitive, and
  #945 established that landing a panel as a layer is exactly that. The docs now
  say the case does not exist, so nothing depends on the answer.
* **Call `RegisterTui` from `BaseTuiRenderer.InitializeAsync`** — #564's other
  recommendation. Rejected twice over: a new axis, which `#555` freezes; and
  insufficient alone, since the interface's own documented sample registers at
  `SidebarRight` and `ShouldRenderPlacement` answers three of four placements
  with `_ => false`, so wiring the call would leave the documented example on the
  dead arm.
* **A broad "backticked type in a fence must exist" rule.** Measured and
  refused — see Known limits, and ADR-011 §2 for the same measurement on a
  larger corpus.
* **Delete `ITuiPlugin`.** `#620`'s `ExtensionAxisFreezeRule` lists it in
  `SealedAxes`, and `EveryDeclaredAxis_IsStillReal` fails the moment a listed
  half stops existing — so deleting the marker means editing #620's guard in the
  same commit. That is #620's decision. #916 deleted the dead `TuiPlugins`
  member and kept the door, which is the same reasoning.

## References

- Issue #564. Merged work it depends on: #780 (the guard and the six teaching
  documents), #966 (the dead `TuiPlugins` member and eight residual prose
  sites), #916, #794, #853.
- `tests/Harbor.Architecture.Tests/CellForgeWidgetAxisRules.cs` — seven rules on
  the view seam, two-sided on the consumer.
- `tools/check-doc-cites.py` — `DOC-PLUGIN-GUIDE-NS-UNDECLARED`,
  `DOC-BASE-LIST-UNIMPLEMENTED`, and the two CI floors.
- [ADR-011](ADR-011-doc-example-compile-gate.md) — why no doc-example compile
  gate exists, and which questions that medium cannot answer.
- [ADR-012](ADR-012-vendored-html-collapse-whitespace-pair.md).
