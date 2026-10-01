# Harbor.Storage.Jsonl.Tests

Tests for **Harbor.Storage.Jsonl**.

## What's covered

JSONL session persistence, append-only writes, compaction round-trip, concurrency

## Run

```bash
dotnet run --project tests/Harbor.Storage.Jsonl.Tests -c Release --no-build -- --minimum-expected-tests 1
```

Or filter to a single test class:

```bash
dotnet run --project tests/Harbor.Storage.Jsonl.Tests -c Release --no-build -- \
  --minimum-expected-tests 1 --treenode-filter "/*/*/JsonlSession/*"
```

## Layer

Tests — depends on the project(s) under test + TUnit (test framework). No production code.

## See also

- [../../docs/ARCHITECTURE_LAYERS.md](../../docs/ARCHITECTURE_LAYERS.md)
- [../../docs/DEVELOPMENT.md](../../docs/DEVELOPMENT.md)
