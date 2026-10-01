# Harbor.Config.Tests

Tests for **Harbor.Abstractions + Harbor.Application**.

## What's covered

Configuration loading, provider config binding, environment variable overrides

## Run

```bash
dotnet run --project tests/Harbor.Config.Tests -c Release --no-build -- --minimum-expected-tests 1
```

Or filter to a single test class:

```bash
dotnet run --project tests/Harbor.Config.Tests -c Release --no-build -- \
  --minimum-expected-tests 1 --treenode-filter "/*/*/Config/*"
```

## Layer

Tests — depends on the project(s) under test + TUnit (test framework). No production code.

## See also

- [../../docs/ARCHITECTURE_LAYERS.md](../../docs/ARCHITECTURE_LAYERS.md)
- [../../docs/DEVELOPMENT.md](../../docs/DEVELOPMENT.md)
