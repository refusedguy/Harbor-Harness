# Harbor.TestKit.Generated

The source-generated test doubles, in an assembly of their own. Issue #1875.

```
<ProjectReference Include="../Harbor.TestKit.Generated/Harbor.TestKit.Generated.csproj"/>
```

## What is here

| Path | What lives there |
|------|------------------|
| `GenerateFakesAttribute.cs` | The opt-in marker: the list of interfaces the generator emits doubles for, and the `[assembly: GenerateFakes(...)]` that carries it. |
| `Generated/FakeCall.cs` | `FakeCall` — one recorded call on a generated double — and `FakeDisposable`, the default value of a generated member whose type is `IDisposable`. |

The twelve `Fake*` classes themselves are NOT in this repository as source: the
generator (`tests/Harbor.TestKit.Generators`) emits them into this assembly at
build time.

## Why it is a separate assembly

Until #1875 the doubles were compiled into the core kit, `Harbor.TestKit`, and
every consumer of the core carried them whether it used them or not. The core
is referenced by nineteen test projects; six of them use a generated double.

The split makes the doubles **opt-in**:

- this part's closure is **three** assemblies — itself and the two contract
  assemblies (`Harbor.Abstractions`, `Harbor.Abstractions.Contracts`);
- the core's closure stays **four**, because the core does NOT reference this
  part;
- a test project that needs a double takes this part as well as the core, and
  its own closure grows by exactly one TEST-ONLY assembly — no product
  assembly is added, because both contract assemblies are already there.

The closure of both parts is pinned by
`tests/Harbor.Architecture.Tests/TestKitCompositionRules.cs`.

## What did NOT change

**The namespace.** The generator hard-codes two names:

- the marker is matched by its METADATA NAME, `Harbor.TestKit.GenerateFakesAttribute`;
- the emitted classes go into `Harbor.TestKit.Generated`.

So the types moved assembly, not namespace. No consumer's `using` line
changed, and no consumer re-runs the generator: the doubles are compiled HERE,
once, and reach a consumer as an ordinary assembly reference.

The consumers that took the new reference in #1875:

| Project | Doubles it uses |
|---|---|
| `Harbor.App.Cli.Tests` | `FakeAgent`, `FakePermissionService`, `FakeProviderRegistry`, `FakeSessionStore` |
| `Harbor.Application.Tests` | `FakeAgentLoop`, `FakeLlmClient`, `FakePermissionService`, `FakeTool` |
| `Harbor.Core.Tests` | `FakeLlmClient`, `FakeTool` |
| `Harbor.Registries.Tests` | `FakeLlmClient`, `FakeTool` |
| `Harbor.TestKit.Generators.Tests` | `FakeSessionStore`, `FakeTool` (the guard for the generator itself) |
| `Harbor.Tools.Builtin.Tests` | `FakeSessionStore`, `FakeTool` |

## The generator edge

The generator is declared here as an ANALYZER:

```xml
<ProjectReference Include="..\Harbor.TestKit.Generators\Harbor.TestKit.Generators.csproj"
                  ReferenceOutputAssembly="false">
  <OutputItemType>Analyzer</OutputItemType>
</ProjectReference>
```

It is build-time only: the generator assembly never lands in this project's
output, so it is not part of the closure, and the repository's own reference
reader (`RepoPaths.ReadProjectReferences`) reports it separately for exactly
that reason. That reader recognises the CHILD-ELEMENT form of `OutputItemType`
that every other analyzer reference in the tree uses, which is why this
project writes it that way.

## The marker is in exactly one assembly

`GenerateFakesAttribute` is declared HERE and nowhere else. Two assemblies
declaring a type of the same full name is CS0433 at every consumer that
references both — so the core dropped its copy in the same commit that this
part appeared.

## The interfaces it opts into

All twelve live in the two contract assemblies, so a double can only ever need
those: `ITool`, `IToolRegistry`, `ISessionStore`, `ISessionContext`,
`ILlmClient`, `IProviderRegistry`, `IAgent`, `IAgentLoop`, `IAgentRegistry`,
`IEventBus` and `IEventBusMiddleware` in `Harbor.Abstractions`,
`IPermissionService` in `Harbor.Abstractions.Contracts`.

An interface that lives deeper does NOT belong in the list: a double for it
would drag that assembly's whole closure into every consumer of this one
(#1809). Such a double belongs to the test project that already has the
closure.
