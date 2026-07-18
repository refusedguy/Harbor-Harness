# Harbor.Plugins.Runtime.Tests

Tests for **Harbor.Plugins.* (full pipeline)**.

## What's covered

End-to-end plugin load: storage -> compilation -> instantiation -> registration -> hosting

## Run

```bash
dotnet test tests/Harbor.Plugins.Runtime.Tests
```

Or filter to a single test class:

```bash
dotnet test tests/Harbor.Plugins.Runtime.Tests --filter "FullyQualifiedName~Plugin"
```

## Layer

Tests — depends on the project(s) under test + TUnit (test framework). No production code.

## See also

- [../../docs/ARCHITECTURE_LAYERS.md](../../docs/ARCHITECTURE_LAYERS.md)
- [../../docs/DEVELOPMENT.md](../../docs/DEVELOPMENT.md)
