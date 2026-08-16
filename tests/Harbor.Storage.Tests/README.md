# Harbor.Storage.Tests

Tests for **Harbor.Storage.Memory + Harbor.Storage.Sqlite**.

## What's covered

Storage contract conformance - same tests against Memory and Sqlite backends

## Run

```bash
dotnet test tests/Harbor.Storage.Tests
```

Or filter to a single test class:

```bash
dotnet test tests/Harbor.Storage.Tests --filter "FullyQualifiedName~Storage"
```

## Layer

Tests — depends on the project(s) under test + TUnit (test framework). No production code.

## See also

- [../../docs/ARCHITECTURE_LAYERS.md](../../docs/ARCHITECTURE_LAYERS.md)
- [../../docs/DEVELOPMENT.md](../../docs/DEVELOPMENT.md)
