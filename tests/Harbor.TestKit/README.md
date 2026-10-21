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
| `TestToolContext.cs` | The one place a `ToolContext` is built (#1775). |
| `TestAgents.cs`, `TestTempDirs.cs`, `TestScalingGate.cs`, `AllocationProbe.cs` | Named entry points other suites share. |

The shared INTERFACE doubles that more than one project needs live one level
down, in [`Harbor.TestKit.Support`](../Harbor.TestKit.Support/) — the
closure-neutral sibling (#1825). `FakeProviderRegistry` is there, next to
`FakeCompactionService`, `FakeTokenTracker`, the auth resolvers and the
rate-limited LLM client, because every one of them needs nothing but the two
contract assemblies. The rule that put them there, and the rule that keeps
the rest of the kit honest, is [Where a double belongs](#where-a-double-belongs).

The source-generated doubles are NOT here (#1875): the opt-in marker, the
call-record type and the twelve emitted `Fake*` classes moved to
[`Harbor.TestKit.Generated`](../Harbor.TestKit.Generated/), which is the part
the generator runs over. The UI-framework doubles (#1549) are NOT here, and
neither is the null render context or the panel provider (#1772): they moved to
[`Harbor.TestKit.Tui`](../Harbor.TestKit.Tui/) in #1889, because they are the
files that need the UI/Desktop stack. This core reaches no UI assembly at
all, which is the whole point of the split: nineteen test projects reference
it, and every product assembly it reached was paid for by all of them.

The shared event and message builders are in neither part: they live in
[`Harbor.TestKit.Support`](../Harbor.TestKit.Support/) as `TestEvents` and
`TestMessages`, because the events and the messages themselves live in
`Harbor.Abstractions.Contracts` and that part's closure is exactly the two
contract assemblies. A test project that publishes an event or builds a
message therefore reaches the builders without gaining a product assembly it
did not already have (#1821, #1816). The message builders moved here from
this core in #1816: the core reaches 20 test projects and this part reaches
33, and the three message-heavy projects that reference only this part
(`Harbor.Benchmarks`, `Harbor.Storage.Tests`, `Harbor.Tui.Tests`) reach the
builders with no csproj edit at all.

## Where a double belongs

The rule the issue #1825 asked for, written down (#1825):

1. **A double that more than one project needs lives in the shared kit.** The
   second project references the kit instead of copying the double. A `Fake*`
   type that exists in a test project while an equivalent exists in the kit is
   a bug — and the equivalence has to be behavioural, not just nominal: two
   doubles for the same interface that answer differently are two answers to
   one question, and a test written against one does not transfer to the
   other.
2. **A double only one project needs stays in that project.** It is that
   project's instrument, and moving it into the kit would make every project
   that links the kit carry a type they never use.
3. **A double whose interface needs a product assembly stays local, and says
   so.** The kit's parts are split by the product stack they reach; a double
   that needs a stack no part reaches has no home in the kit at all. `IModelCatalog`
   is the worked example: it is declared in `Harbor.Providers.OpenAiCompatible`,
   so its double lives in the project that needs it and says why in its
   header.
4. **A behavioural double — one with rate limiting, retries, chaos — belongs
   in the kit too, in a file whose name says what it does.**
   `Fakes/ILlmClient.RateLimited.cs` is the worked example: the
   rate-limited client and its token bucket are a pair, and the pair moves
   together.
5. **The name says whose contract it stands for.** `Fake*` for an interface
   Harbor owns, `Stub*` for one that is not (a BCL or a foreign SDK),
   `Scripted*` for a double that replays a pre-arranged sequence.

`tests/Harbor.Architecture.Tests/TestKitLocalDoubleRules.cs` enforces rule 1
mechanically: a project outside the kit that declares a TOP-LEVEL double for
one of the consolidated interfaces is reported unless the file is named in the
rule's exception list, with the reason it stays. A double NESTED inside one
test class is that test's instrument and is not judged.

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

The support part's own closure is THREE assemblies — itself and the two
contract assemblies — and #1825 did not move it. The shared doubles that
moved into it (the provider registry, the compaction service, the token
tracker, the auth resolvers, the rate-limited LLM client) were chosen for
exactly that reason: every one of them needs nothing but `Harbor.Abstractions`
and `Harbor.Abstractions.Contracts`. A double that needed a product assembly
would have gone to a part that reaches that assembly, or stayed in the
project that needs it.

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
