# Harbor.TestKit.Generators

Roslyn source generator that emits **concrete test doubles** for the interfaces a
test assembly opts into. Issue #1769.

## Layer

**Build-time tooling.** A Roslyn component consumed as an ANALYZER by
`tests/Harbor.TestKit`. It ships no runtime code: the generator assembly is not
referenced by anything (`ReferenceOutputAssembly="false"`), and the doubles it
emits are ordinary C# classes.

## What it is NOT

- **Not a proxy.** `DispatchProxy` is `[RequiresDynamicCode]` (IL3050) and
  Castle emits IL at run time; both are banned by
  `docs/ARCHITECTURE_LAYERS.md` §5.8 and by the AOT rules. A generated class is
  ordinary code — AOT-safe, checked by the compiler, no run-time dependency.
- **Not reflection.** The generator reads the interface from the COMPILATION
  (`INamedTypeSymbol`) and writes a class that implements it. Nothing at run
  time looks anything up by name.

## How it is wired

| File | Purpose |
|------|---------|
| `FakeGenerator.cs` | `IIncrementalGenerator` that reads the opt-in list and emits one class per interface. |
| `FakeDiagnostics.cs` | The `HARBORFAKE00x` diagnostics — every one of them an **error**. |

The opt-in marker lives in the CONSUMED assembly
(`tests/Harbor.TestKit/GenerateFakesAttribute.cs`) and is matched by **metadata
name** — the same convention `src/Harbor.CodeGen/ToolGlueGenerator.cs` uses for
`ToolInfoAttribute` — so this component needs no project reference to
`Harbor.TestKit`:

```csharp
[assembly: GenerateFakes(typeof(ITool), typeof(ISessionStore), /* … */)]
```

The doubles are emitted into `Harbor.TestKit.Generated`, named
`Fake{Interface}`:

```csharp
public partial class FakeTool : global::Harbor.Abstractions.Tools.ITool
```

## The shape of an emitted double

| Member kind | Emitted as | Default |
|---|---|---|
| property | `public virtual T Name { get; set; }` | `default` / `null!` / `[]` — see below |
| method | `public virtual T M(…)` + a configurable `{Member}Value` / `{Member}Result` property | the configurable property's value |
| `Task` / `void` | `public virtual …` | `Task.CompletedTask` / no-op |
| `IAsyncEnumerable<T>` | `public virtual …` | an empty stream |
| default interface member | **not emitted** | the interface's own default |

The defaults are chosen to fail LOUDLY, never silently:

- a member that returns a `Result` / `Result<T>` defaults to
  `Result.Failure("… is not configured by this test double.")` — because
  `default(Result<T>)` is a **success carrying a null value**, which is the
  worst possible default: it looks like the member worked;
- a reference-typed property defaults to `null!` — a value object (a
  `ToolName`, a `ProviderId`) has to be set by the test that reads it;
- a collection defaults to `[]`, a `JsonDocument` to `{"type":"object"}`, a
  `Channel<T>` to an unbounded channel, an `IDisposable` to a no-op.

Every call can be recorded in `RecordedCalls` (a `FakeCall` per call: the
member name and its arguments). The recording is **opt-in**
(`RecordCalls = true`): a record allocates on every call, and a double that
sits in a measured path must cost nothing. The check is at the call site, so
a disabled recording allocates nothing at all.

An **overloaded** member name gets no configurable property: two overloads
would generate the same `{Member}Result`, and a numbered suffix is a trap. An
overload returns the generated default inline; a test that needs a different
answer overrides that overload.

## Diagnostics

| Id | Meaning |
|---|---|
| `HARBORFAKE001` | The opt-in list named a type that is not an interface. |
| `HARBORFAKE002` | The interface uses a shape the generator does not implement (a static abstract member, a by-ref return, a pointer parameter, a generic interface). |
| `HARBORFAKE003` | The same interface was listed twice. |
| `HARBORFAKE004` | Two members would generate the same name. |

All four are **errors**: a shape the generator cannot express must fail the
build loudly, never emit half a double that compiles into something silently
wrong.

## Tests

`tests/Harbor.TestKit.Generators.Tests`:

- `FakeGeneratorDriverTests` runs the generator IN-PROCESS over a synthetic
  compilation. The positive control asserts that the emitted double makes the
  resulting compilation **error-free** (that is what proves it really
  implements the interface); the negative control asks for an interface with a
  static abstract member and asserts the diagnostic and that nothing is emitted.
- `GeneratedDoubleTests` exercises the doubles the generator actually emitted
  into `Harbor.TestKit`. If the generator did not run, that file does not
  compile — the guard cannot pass against a generator that produced nothing.

## Build

```bash
dotnet build tests/Harbor.TestKit.Generators/Harbor.TestKit.Generators.csproj
```

## Known limitations

- A generic interface is refused (`HARBORFAKE002`).
- A static abstract member, a by-ref return and a pointer parameter are
  refused (`HARBORFAKE002`).
- The generated doubles live in `Harbor.TestKit`, so every interface they
  implement must be visible to `Harbor.TestKit` — an interface that lives deeper
  (say `IConfigStore` in `Harbor.Application`) does not belong in the opt-in
  list: a double for it would drag that assembly's closure into every consumer
  of `Harbor.TestKit` (#1809).
