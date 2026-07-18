# Harbor.Tui.Tests

Tests for **Harbor.Tui.Abstractions + renderers**.

## What's covered

TUI unit tests - UiReducer state folding, UiStore, view-model behavior, command dispatch

## Run

```bash
dotnet test tests/Harbor.Tui.Tests
```

Or filter to a single test class:

```bash
dotnet test tests/Harbor.Tui.Tests --filter "FullyQualifiedName~UiReducer"
```

## Layer

Tests — depends on the project(s) under test + TUnit (test framework). No production code.

## See also

- [../../docs/ARCHITECTURE_LAYERS.md](../../docs/ARCHITECTURE_LAYERS.md)
- [../../docs/DEVELOPMENT.md](../../docs/DEVELOPMENT.md)
