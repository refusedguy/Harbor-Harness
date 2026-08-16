# Harbor.Config.Tests

Tests for **Harbor.Abstractions + Harbor.Core**.

## What's covered

Configuration loading, provider config binding, environment variable overrides

## Run

```bash
dotnet test tests/Harbor.Config.Tests
```

Or filter to a single test class:

```bash
dotnet test tests/Harbor.Config.Tests --filter "FullyQualifiedName~Config"
```

## Layer

Tests — depends on the project(s) under test + TUnit (test framework). No production code.

## See also

- [../../docs/ARCHITECTURE_LAYERS.md](../../docs/ARCHITECTURE_LAYERS.md)
- [../../docs/DEVELOPMENT.md](../../docs/DEVELOPMENT.md)
