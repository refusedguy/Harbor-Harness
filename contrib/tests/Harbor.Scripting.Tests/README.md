# Harbor.Scripting.Tests

Tests for **Harbor.Scripting.* (full pipeline)**.

## What's covered

End-to-end script load: storage -> compilation -> engines -> bridge -> hosting

## Run

```bash
dotnet test tests/Harbor.Scripting.Tests
```

Or filter to a single test class:

```bash
dotnet test tests/Harbor.Scripting.Tests --filter "FullyQualifiedName~Script"
```

## Layer

Tests — depends on the project(s) under test + TUnit (test framework). No production code.

## See also

- [../../docs/ARCHITECTURE_LAYERS.md](../../docs/ARCHITECTURE_LAYERS.md)
- [../../docs/DEVELOPMENT.md](../../docs/DEVELOPMENT.md)
