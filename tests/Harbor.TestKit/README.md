# Harbor.TestKit

The shared fixture library for Harbor's test tree. Referenced by every test
project that needs a double for something Harbor owns; never shipped.

```
<ProjectReference Include="../Harbor.TestKit/Harbor.TestKit.csproj"/>
```

## Layout

| Path | What lives there |
|------|------------------|
| `Fakes/<IInterface>.cs` | One file per interface: the double that implements it. The file is named after the interface, so "where do I put my double" has exactly one answer. |
| `Builders/` | Builders, not doubles — message factories and the like. |
| `TestToolContext.cs` | The one place a `ToolContext` is built (#1775). |
| `TestAgents.cs`, `TestMessages.cs`, `TestTempDirs.cs`, `TestScalingGate.cs`, `AllocationProbe.cs` | Named entry points other suites share. |

The source-generated doubles are NOT here (#1875): the opt-in marker, the
call-record type and the twelve emitted `Fake*` classes moved to
[`Harbor.TestKit.Generated`](../Harbor.TestKit.Generated/), which is the part
the generator runs over. The UI-framework doubles (#1549) are NOT here, and
neither is the null render context or the panel provider (#1772): they moved to
[`Harbor.TestKit.Tui`](../Harbor.TestKit.Tui/) in #1889, because they are the
files that need the UI/Desktop stack. This core reaches no UI assembly at
all, which is the whole point of the split: nineteen test projects reference
it, and every product assembly it reached was paid for by all of them.

The shared event builders are in neither part: they live in
[`Harbor.TestKit.Support`](../Harbor.TestKit.Support/) as `TestEvents`, because
the events themselves live in `Harbor.Abstractions.Contracts` and that part's
closure is exactly the two contract assemblies. A test project that publishes
an event therefore reaches the builders without gaining a product assembly it
did not already have (#1821).

## Naming

- `Fake*` — a double for an interface **Harbor owns**. It implements the real
  contract; it does not approximate it.
- `Stub*` — a double for something **outside** Harbor (a BLL type, a foreign
  SDK) where the shape is fixed by the other side.
- `Scripted*` — a double with a **scripted** behaviour: it replays a
  pre-arranged sequence and records what it was asked.

## Rules

1. **A double lives in the shared kit, not in the test project that needed
   it.** If two projects need the same double, the second one references the
   first instead of copying it. A `Fake*` type that exists in a test project
   while an equivalent exists in the kit is a bug.
2. **One file = one interface.** `Fakes/ISessionStore.cs` holds
   `FakeSessionStore` and nothing else. The name of the file is the interface
   the double stands in for.
3. **No helper without a caller.** Every public member of this library is used
   somewhere in `tests/`. A member nobody calls is dead weight in a library
   every project links — see #1774, which removed the hand-written
   `TestAsserts` family for exactly this reason.
4. **A double must not change the behaviour it stands in for.** A fake that
   answers differently from the production implementation is a test that lies.
5. **A double that needs a heavy dependency belongs in a PART, not here.**
   `Harbor.TestKit` is the core: it references `Harbor.Abstractions` and
   `Harbor.Abstractions.Contracts` and nothing else, so its closure is four
   assemblies — itself, its own support part, and the two contract assemblies —
   and every one of its consumers inherits that. A double that
   needs the UI/Desktop stack goes into a part (`Harbor.TestKit.Tui`), and only
   the consumers that actually use it take the reference.
6. **A double the GENERATOR emits belongs in the generated part, not here.**
   `Harbor.TestKit.Generated` (#1875) carries the opt-in marker and the twelve
   emitted classes. The core does not reference that part: a reference would
   make the core's closure five, and every consumer of the core would carry
   the doubles whether it used them or not.

## Dependency closure

The core `Harbor.TestKit` references `Harbor.Abstractions`,
`Harbor.Abstractions.Contracts` and its own support part
`Harbor.TestKit.Support` (#1772/#1773 — the core references the sibling so a
consumer of the core reaches the shared helpers transitively, in the same
`Harbor.TestKit` namespace). Its transitive `<ProjectReference>` closure is
therefore **four** assemblies, and every consumer inherits exactly that. Only
TWO of the four are product assemblies, and both are contract assemblies every
consumer already has.

The core carries NO build-time-only reference any more (#1875). The source
generator (`Harbor.TestKit.Generators`, #1769) used to be declared here as
`OutputItemType=Analyzer` with `ReferenceOutputAssembly=false`; it now runs
over [`Harbor.TestKit.Generated`](../Harbor.TestKit.Generated/) instead, and
this project declares neither it nor that part.

Before #1889 the kit also referenced the `Harbor.Ui.Framework.*` family and
`Harbor.Desktop.Abstractions`, because `UiTestDoubles.cs` needed them. That
made the closure **nineteen** assemblies, eleven of them UI/Desktop, and
every one of the kit's fourteen consumers paid for them whether it used a
double or not — `Harbor.Core.Tests` alone carried thirteen assemblies it had
no use for. The doubles moved to [`Harbor.TestKit.Tui`](../Harbor.TestKit.Tui/)
and the core became what it says above.

The rule this replaced was "the closure of `Harbor.TestKit` must stay a
subset of the closure of every project that references it", which was a
restatement of the problem: the kit could not grow a double that needed
anything new, because every consumer would grow with it. The composite kit
removes the reason for the constraint — a part is opt-in, and a consumer that
does not use the doubles does not compile them.

The split is pinned by `tests/Harbor.Architecture.Tests/TestKitCompositionRules.cs`:
the core may not reach `Harbor.Ui.Framework*`, `Harbor.Desktop.*`,
`Harbor.Terminal.Abstractions` or `Harbor.Application`, and the parts may.
The same file pins the generated part (#1875): its closure is exactly itself
plus the two contract assemblies, it reaches none of the forbidden projects,
and the core does not reference it.
