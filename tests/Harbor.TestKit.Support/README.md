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
| `TempDirScope.cs`, `EnvScope.cs`, `GitProbe.cs` | The three scopes: a temp directory, an environment variable, a git repository. |
| `StubHttpMessageHandler.cs` | The HTTP handler double (#1825). |
| `Exhaustive.cs`, `TestGitQuery.cs`, `TestEventBusMiddleware.cs`, `TestRepoPaths.cs`, `AppConfigDocument.cs` | The rest of the shared helpers. |

## Where a double belongs

The placement rule is written down in
[`Harbor.TestKit/README.md`](../Harbor.TestKit/README.md#where-a-double-belongs).
In short: a double that more than one project needs lives here or in the
core; a double only one project needs stays with that project; a double whose
interface needs a product assembly this part may not reach stays local and
says why in its header.

`tests/Harbor.Architecture.Tests/TestKitLocalDoubleRules.cs` enforces the
first half of that mechanically.
