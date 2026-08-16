# Harbor.Tui.E2E.Tests

Tests for **Harbor.Tui.* renderers**.

## What's covered

End-to-end TUI smoke tests - feed a scripted agent event stream, capture rendered output, assert key bytes present

## Run

```bash
dotnet test tests/Harbor.Tui.E2E.Tests
```

Or filter to a single test class:

```bash
dotnet test tests/Harbor.Tui.E2E.Tests --filter "FullyQualifiedName~E2E"
```

## Layer

Tests — depends on the project(s) under test + TUnit (test framework). No production code.

## See also

- [../../docs/ARCHITECTURE_LAYERS.md](../../docs/ARCHITECTURE_LAYERS.md)
- [../../docs/DEVELOPMENT.md](../../docs/DEVELOPMENT.md)
