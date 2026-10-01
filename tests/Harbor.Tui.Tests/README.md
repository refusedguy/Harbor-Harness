# Harbor.Tui.Tests

Tests for **Harbor.Tui.Abstractions + renderers**.

## What's covered

TUI unit tests - AppReducer state folding, UiStore, view-model behavior, command dispatch

## Run

```bash
dotnet run --project tests/Harbor.Tui.Tests -c Release --no-build -- --minimum-expected-tests 1
```

Or filter to a single test class:

```bash
dotnet run --project tests/Harbor.Tui.Tests -c Release --no-build -- \
  --minimum-expected-tests 1 --treenode-filter "/*/*/AppReducer/*"
```

## Layer

Tests — depends on the project(s) under test + TUnit (test framework). No production code.

## See also

- [../../docs/ARCHITECTURE_LAYERS.md](../../docs/ARCHITECTURE_LAYERS.md)
- [../../docs/DEVELOPMENT.md](../../docs/DEVELOPMENT.md)
