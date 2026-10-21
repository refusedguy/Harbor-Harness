# Harbor.Arch.Analyzers

Internal Roslyn analyzer for Harbor. Holds the repository's OWN architecture rules, written as compiler extensions instead of regex scans over the source text. Packaged as a Roslyn component (`IsRoslynComponentPackage=true`), not a runtime library.

Issue #1842. The pilot that measured the substrate is `tests/Harbor.Architecture.Tests/Roslyn/`; the promotion this project performs is the pilot's own written-down promotion path.

## Layer

**Build-time tooling.** No runtime dependency; the compiler loads it and runs its rules over every product project while compiling it.

## What's in it

| File | Purpose |
|------|---------|
| `NoDynamicCodeAnalyzer.cs` | `HARCH001` — no dynamic-code API (no `Assembly.Load`, no `AssemblyLoadContext`, nothing from the emit namespace) in product code. The compiler-side version of the ban that `BannedSymbols.DynamicCode.txt` states by name, and the analyzer version of the matcher half of `tests/Harbor.Architecture.Tests/ReflectionConventionRule.cs`. |
| `NamespaceOwnershipAnalyzer.cs` | `HARCH002` — a namespace root belongs to the project that declares it. The analyzer version of the R1/R2 half of `tests/Harbor.Architecture.Tests/AbstractionsNamespaceOwnershipRules.cs`. |

## Public API summary

- `NoDynamicCodeAnalyzer : DiagnosticAnalyzer` — reports under `HARCH001`.
- `NamespaceOwnershipAnalyzer : DiagnosticAnalyzer` — reports under `HARCH002`.
- One diagnostic id per rule. The ids are per rule rather than per severity because the severity is configured by id: a rule that cannot be switched off without switching off its neighbours gets switched off together with them.

## Wiring

| Consumer | How it gets the analyzer |
|----------|---------------------------|
| `src/**` | `src/Directory.Build.props` adds the project reference with `OutputItemType=Analyzer` and `ReferenceOutputAssembly=false`, so the assembly reaches the compiler and never becomes a runtime reference of the consuming project. The plugin family is exempted from `HARCH001` by id, because loading code at run time IS the plugin product. |
| `apps/**` | `apps/Directory.Build.props` adds the same reference. |
| `tests/Harbor.Architecture.Tests` | references the project as an ordinary library, because the tests drive the analyzers in-process over a compilation they build. That is also what puts the project into the build closure the architecture suite measures. |

The severity of each rule is stated in the repository `.editorconfig`, so a rule can be turned off for one project without touching its neighbours.

## Dependencies

| Package | Purpose |
|---------|---------|
| `Microsoft.CodeAnalysis.CSharp` (5.9.0, per `Directory.Packages.props`) | The compiler API: `DiagnosticAnalyzer`, `AnalysisContext`, the semantic model. `PrivateAssets=all` — the analyzer runs inside the compiler, which already hosts the Roslyn runtime. The version is NOT written here: the central pin is the only place it is stated, and a second statement of it is how a restore ends up with an exact constraint that conflicts with the pin (NU1608). The pin matches the compiler the build runs on (SDK 10.0.402 ships Roslyn 5.9.0) — see #1897. |

## Tests

`tests/Harbor.Architecture.Tests/Roslyn/` — the planted controls (an offender the analyzer must report, a near-miss it must not), the differential against the text rule the analyzer replaces, and the check that the analyzer is actually wired into the product build.

## Build

```bash
dotnet build src/Harbor.Arch.Analyzers/Harbor.Arch.Analyzers.csproj
```

## Known limitations

- `HARCH001` does not flag the value types of the emit namespace (`OpCode`, `ILOpCode`, `AssemblyBuilderAccess`): they describe emitted IL rather than emitting it. The text rule it replaces flags them, because it matched the namespace token.
- `HARCH002` knows two owned roots. A new owned root is a row in the ownership table, and a row nobody reads is a rule that has stopped judging.
- The two rules that are still hosted by the architecture test project (`HARCH003`, `HARCH004`) are not promoted here; the order in which they follow is written down in the issue.
