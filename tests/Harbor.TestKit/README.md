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
| `UiTestDoubles.cs` | The UI-framework doubles (#1549) — kept in one file because they extend UI base types, not Harbor interfaces. |

## Naming

- `Fake*` — a double for an interface **Harbor owns**. It implements the real
  contract; it does not approximate it.
- `Stub*` — a double for something **outside** Harbor (a BLL type, a foreign
  SDK) where the shape is fixed by the other side.
- `Scripted*` — a double with a **scripted** behaviour: it replays a
  pre-arranged sequence and records what it was asked.

## Rules

1. **A double lives in `Harbor.TestKit/`, not in the test project that needed
   it.** If two projects need the same double, the second one references the
   first instead of copying it. A `Fake*` type that exists in a test project
   while an equivalent exists here is a bug.
2. **One file = one interface.** `Fakes/ISessionStore.cs` holds
   `FakeSessionStore` and nothing else. The name of the file is the interface
   the double stands in for.
3. **No helper without a caller.** Every public member of this library is used
   somewhere in `tests/`. A member nobody calls is dead weight in a library
   every project links — see #1774, which removed the hand-written
   `TestAsserts` family for exactly this reason.
4. **A double must not change the behaviour it stands in for.** A fake that
   answers differently from the production implementation is a test that lies.

## Dependency closure

`Harbor.TestKit` references the `Harbor.Ui.Framework.*` family and
`Harbor.Desktop.Abstractions` because `UiTestDoubles.cs` needs them. That is
safe only while every consumer already has them in its own closure: the
closure of `Harbor.TestKit` must stay a **subset** of the closure of every
project that references it. Adding `Harbor.TestKit` to a project that does not
already reference the UI-framework family would push new product assemblies
into that project — don't. Either the double does not need them, or the
consumer already has them.
