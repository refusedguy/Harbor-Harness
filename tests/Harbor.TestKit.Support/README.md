# Harbor.TestKit.Support

The closure-neutral part of the shared test kit: the helpers and the shared
INTERFACE doubles that need no product assembly beyond the two contract
assemblies.

```
<ProjectReference Include="../Harbor.TestKit.Support/Harbor.TestKit.Support.csproj"/>
```

## Why a separate part

The core kit, [`Harbor.TestKit`](../Harbor.TestKit/), is the shared fixture
library, but it drags its own references in with it, and every consumer pays
for that. This part references `Harbor.Abstractions` and
`Harbor.Abstractions.Contracts` — and nothing else — so a test project that
takes it gains **no product assembly it did not already have**. That is what
makes it the part every test project can take.

Its transitive `<ProjectReference>` closure is therefore three assemblies:
itself and the two contract assemblies. The core kit's is four (it adds
itself), and both are pinned by
`tests/Harbor.Architecture.Tests/TestKitCompositionRules.cs`.

The namespace is `Harbor.TestKit`, so `using Harbor.TestKit;` is all a
consumer needs regardless of which part the type lives in.

## What lives here

| Path | What lives there |
|------|------------------|
| `Fakes/<IInterface>.cs` | The shared double for that interface: the provider registry, the compaction service, the token tracker, the auth resolvers, the rate-limited LLM client. |
| `TestEvents.cs`, `TestMessages.cs` | The event and message builders (#1821, #1816). |
| `TestArgs.cs`, `TestToolArgs.cs`, `TestJson.cs`, `TestJsonContext.cs` | The shared argument and JSON helpers. |
| `CapturingLogger.cs`, `EmptyServiceProvider.cs`, `ManualTimeProvider.cs` | The logging, container and clock doubles. |
| `TestSeeds.cs` | The single seed source for stochastic tests, overridable from the environment. |
| `FuzzLoop.cs` | The reusable fuzz driver: a seeded walk whose violations carry the seed, case index and exact input (#2704 P03). |
| `Arbitrary.cs` | The seeded agent-event transcript generator: a well-formed walk for a given `Random` and turn count (#2704 D02). |
| `TempDirScope.cs`, `EnvScope.cs`, `GitProbe.cs` | The three scopes: a temp directory, an environment variable, a git repository. |
| `UseCultureAttribute.cs` | The per-test culture fixture: pins `CurrentCulture`/`CurrentUICulture` and restores them on exit (#2704 T05). |
| `StubHttpMessageHandler.cs` | The HTTP handler double (#1825). |
| `Exhaustive.cs`, `TestGitQuery.cs`, `TestEventBusMiddleware.cs`, `TestRepoPaths.cs`, `AppConfigDocument.cs` | The rest of the shared helpers. |
| `DrainableWorker.cs` | The background-work gate for tests: `DrainAsync()` waits for queued AND in-flight items (#2673 A17). |

## Reproducing a stochastic failure

`TestSeeds.Values(defaults)` is the one place a fuzz, property or data-driven
test reads its seeds. With no override it returns the defaults the test always
used. `HARBOR_TEST_SEED` narrows a run to that one seed, and
`HARBOR_TEST_ITERATIONS` walks forward from it:

```
HARBOR_TEST_SEED=99 dotnet run --project tests/Harbor.Tui.CellForge.Tests -c Release --no-build -- --treenode-filter "/*/*/DiffEngineFuzzTests/*"
HARBOR_TEST_ITERATIONS=20 dotnet run --project tests/Harbor.Ipc.Tests -c Release --no-build -- --treenode-filter "/*/*/ResilientFrameReaderFuzzTests/*"
```

Both fuzz suites put the resolved seed in their failure message, so a red run
can be replayed without guessing which input failed. `FuzzLoop<TInput>`
enforces that shape for any new suite: its `FuzzViolationException` carries the
loop name, seed, case index, count and the failing bytes.

## Where a double belongs

The placement rule is written down in
[`Harbor.TestKit/README.md`](../Harbor.TestKit/README.md#where-a-double-belongs).
In short: a double that more than one project needs lives here or in the
core; a double only one project needs stays with that project; a double whose
interface needs a product assembly this part may not reach stays local and
says why in its header.

`tests/Harbor.Architecture.Tests/TestKitLocalDoubleRules.cs` enforces the
first half of that mechanically.
