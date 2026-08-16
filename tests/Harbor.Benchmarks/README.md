# Harbor.Benchmarks

Tests for **Harbor.Core + Harbor.Storage.Jsonl**.

## What's covered

BenchmarkDotNet micro-benchmarks - AgentLoop iteration, JSONL serialization, registry lookups

## Run

```bash
dotnet test tests/Harbor.Benchmarks
```

Or filter to a single test class:

```bash
dotnet test tests/Harbor.Benchmarks --filter "FullyQualifiedName~Benchmark"
```

## Layer

Tests — depends on the project(s) under test + TUnit (test framework). No production code.

## See also

- [../../docs/ARCHITECTURE_LAYERS.md](../../docs/ARCHITECTURE_LAYERS.md)
- [../../docs/DEVELOPMENT.md](../../docs/DEVELOPMENT.md)
