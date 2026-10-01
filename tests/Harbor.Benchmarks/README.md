# Harbor.Benchmarks

BenchmarkDotNet micro-benchmarks for the Harbor hot paths — event bus, IPC
framing/broadcast, JSONL + SQLite stores, registries, streaming coalescer,
prompt building, projection and cell-diff rendering.

## What's covered

- ~39 benchmark classes / 120+ rows, all `[MemoryDiagnoser]`, Release, zero warnings.
- Every class carries its own **7-field measurement contract** in its doc comment
  (#408): `Operation` / `Payload` / `StateReset` / `Drain` / `RetainedState` /
  `AwaitSemantics` / `AllocAttribution`. Read it before quoting a number — it says
  what the row includes (e.g. whether a consumer drain is folded in).
  The contract is enforced by `BenchmarkContractTests` in
  `tests/Harbor.Architecture.Tests`.
- The three event-bus/IPC files report **enqueue-only**, **enqueue + consumer
  drain** and **steady state** as separate rows, so no single "bus throughput"
  number silently mixes delivery in.

## Run

```bash
# whole suite
dotnet run -c Release --project tests/Harbor.Benchmarks

# one class
dotnet run -c Release --project tests/Harbor.Benchmarks -- --filter '*ToolRegistry*'

# list what is available
dotnet run -c Release --project tests/Harbor.Benchmarks -- --list flat
```

This project is **not** a test project — it is a console app that BenchmarkDotNet
drives, so `dotnet test` has nothing to discover here. (An earlier version of
this line added "and discovers zero tests across the repo in general"; that
generalisation was wrong — see
[CONTRIBUTING.md §Why not `dotnet test`](../../CONTRIBUTING.md#why-not-dotnet-test).
The supported way to run any suite here is a plain executable, e.g.
`dotnet run --project tests/<Project> -c Release --no-build -- --minimum-expected-tests 1`.)
Results land in `BenchmarkDotNet.Artifacts/` under the process working
directory.

Numbers are tracked in [../../docs/BENCHMARKS.md](../../docs/BENCHMARKS.md),
which records machine + date (or `[CI-short]`) for every row it quotes.

## Layer

Benchmarks — depends on the projects under test + BenchmarkDotNet. No production
code lives here; this project references `src/` projects but is referenced by none.

## See also

- [../../docs/BENCHMARKS.md](../../docs/BENCHMARKS.md)
- [../../docs/ARCHITECTURE_LAYERS.md](../../docs/ARCHITECTURE_LAYERS.md)
- [../../docs/DEVELOPMENT.md](../../docs/DEVELOPMENT.md)
