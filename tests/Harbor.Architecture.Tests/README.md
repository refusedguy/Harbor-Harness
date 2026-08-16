# Harbor.Architecture.Tests

Tests for **Harbor (all src projects)**.

## What's covered

Layering invariants - Domain/Application/Infrastructure/Presentation edges, NetArchTest rules + reflection-based fallback

## Run

```bash
dotnet test tests/Harbor.Architecture.Tests
```

Or filter to a single test class:

```bash
dotnet test tests/Harbor.Architecture.Tests --filter "FullyQualifiedName~LayerDependency"
```

## Layer

Tests — depends on the project(s) under test + TUnit (test framework). No production code.

## See also

- [../../docs/ARCHITECTURE_LAYERS.md](../../docs/ARCHITECTURE_LAYERS.md)
- [../../docs/DEVELOPMENT.md](../../docs/DEVELOPMENT.md)
