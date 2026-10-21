# Harbor.TestKit.Tui

The UI-scoped part of Harbor's shared test kit: the doubles that substitute
a UI seam. Referenced by the test projects that actually use one; never
shipped.

```
<ProjectReference Include="../Harbor.TestKit.Tui/Harbor.TestKit.Tui.csproj"/>
```

## Why this is a separate project

`Harbor.TestKit` is referenced by fourteen test projects, and every product
assembly it reaches is paid for by all of them. Before #1889 the core carried
the four UI references (`Harbor.Ui.Framework.Services`, `.State`, `.ViewModels`
and `Harbor.Desktop.Abstractions`), so its transitive `<ProjectReference>`
closure was nineteen assemblies — eleven of them UI/Desktop — and eleven test
projects that never used a UI double compiled them anyway.

The files that need the UI stack therefore live here, and the core reaches
only `Harbor.Abstractions` and `Harbor.Abstractions.Contracts` (plus the
kit's own `Harbor.TestKit.Support` sibling, which is not a product assembly).
`tests/Harbor.Architecture.Tests/TestKitCompositionRules.cs` pins that split:
the core may not reach `Harbor.Ui.Framework*`, `Harbor.Desktop.*`,
`Harbor.Terminal.Abstractions` or `Harbor.Application`, and this part may.

## What lives here

| Path | What lives there |
|------|------------------|
| `FakeTerminalBackend.cs` | The terminal-backend double (#1772) — it substitutes `ISyncTerminalBackend`, which lives in `Harbor.Tui.CellForge.Engine`. |
| `UiTestDoubles.cs` | `TestDispatcherAdapter`, `CountingStoreSubscriberStub`, `CountingChatViewModelStub`, `DiffViewModelTestStub` (#1549) — they extend UI base types, so they are kept in one file. |
| `NullRenderContext.cs` | A render context that renders nothing (#1772) — it implements `ITuiRenderContext`, which lives in `Harbor.Terminal.Abstractions`. |
| `TestPanelProvider.cs` | A panel provider that builds a declared widget (#1772) — it implements `IPanelProvider`, which lives in `Harbor.Ui.Framework.State`. |
| `TestColors.cs` | The packed-colour helpers the glow suites share (`Channels`, `HotTone`) — not a double, but it reaches `Harbor.Tui.CellForge.Engine` for the colour type, and the core kit may not (#1889). |

## The namespace is `Harbor.TestKit`

The TestKit family is ONE namespace spread over several assemblies, so a
consumer needs `using Harbor.TestKit;` and nothing else, whichever assembly
the type happens to live in. That is the convention #1772/#1773 set, and this
part follows it.

## Rules

1. **Only a type that needs the UI stack belongs here.** A double that
   needs only `Harbor.Abstractions` belongs in the core
   [`Harbor.TestKit`](../Harbor.TestKit/README.md), which every consumer
   already references.
2. **A consumer takes this reference only when it uses one of these
   types.** The reference is the whole point of the split: the projects that
   never use a UI double must not compile the UI stack.
3. **The composition is pinned.**
   `tests/Harbor.Architecture.Tests/TestKitCompositionRules.cs` asserts that
   the core kit reaches no UI/Desktop/Terminal/Application project, and that
   this part may.

## Dependency closure

This project references `Harbor.Ui.Framework.Services`,
`Harbor.Ui.Framework.State`, `Harbor.Ui.Framework.ViewModels`,
`Harbor.Desktop.ViewModels` (#1899 moved the desktop view-models out of
`Harbor.Desktop.Abstractions`), `Harbor.Terminal.Abstractions` and
`Harbor.Tui.CellForge.Engine`. `Harbor.Desktop.ViewModels` is where
`ChatViewModelBase` and `DiffViewModelBase` live; #1835 owns moving
them into `Harbor.Ui.Framework.ViewModels`, and the edge goes with them.
