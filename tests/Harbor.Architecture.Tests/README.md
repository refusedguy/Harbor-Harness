# Harbor.Architecture.Tests

Tests for **Harbor (all src projects)**.

## What's covered

Two halves of the layering contract, both mechanically enforced:

1. **Reference rules** — Domain/Application/Infrastructure/Presentation *edges*:
   `LayerDependencyTests`, `NetArchLayerRules`, `AbstractionsSplitLayerRules`,
   `FullLayerMatrixTests`, `CellForgeGraphRules`.
2. **Capability rules** — which I/O capabilities a Presentation assembly may
   exercise: `PresentationCapabilityRules` (issue #455). Walks each Presentation
   assembly's IL with Mono.Cecil and fails on subprocess spawn, `System.IO.File`,
   `System.IO.Directory`, network and reflection emit. Existing violations are
   held in an enforced `KnownViolations` baseline with a tracking issue per site.
   Capabilities an assembly owns for good and will never give up — the console
   device a renderer reads — live in a separate `PermanentCapabilities` table
   valued by their reason, not a fix that will never be scheduled (#669).
   See ARCHITECTURE_LAYERS.md §3 and §5.6.
3. **Single-source rules** — a domain fact decided in one place, not re-derived
   per consumer: `SessionStatusSourceRule` (#687, a status is not re-derived from
   the transcript), `SessionStatusTableRule` (#663, one label/brush table),
   `DiagnosticsClassificationRule` (#674, the core classifies diagnostics),
   `CostPricedInCoreRules` (#653, money is priced in the core). These are
   repository text scans over `src/` + `apps/`, each with a liveness check and a
   positive control.

## Run

> `dotnet test` discovers zero tests in this repo (broken MTP bridge). Run the
> test project as a plain executable:

```bash
dotnet build tests/Harbor.Architecture.Tests/Harbor.Architecture.Tests.csproj
dotnet run --project tests/Harbor.Architecture.Tests -c Release --no-build -- --minimum-expected-tests 1
```

Or filter to a single test class (TUnit uses `--treenode-filter`):

```bash
dotnet run --project tests/Harbor.Architecture.Tests -c Release --no-build -- --treenode-filter "/*/*/PresentationCapabilityRules/*"
```

## Layer

Tests — depends on the project(s) under test + TUnit (test framework). No production code.

## See also

- [../../docs/ARCHITECTURE_LAYERS.md](../../docs/ARCHITECTURE_LAYERS.md)
- [../../docs/DEVELOPMENT.md](../../docs/DEVELOPMENT.md)
