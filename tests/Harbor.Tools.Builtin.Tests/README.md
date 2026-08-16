# Harbor.Tools.Builtin.Tests

Tests for **Harbor.Tools.Builtin**.

## What's covered

Builtin tools - read, write, edit, bash, glob, grep, ls, task

## Run

```bash
dotnet test tests/Harbor.Tools.Builtin.Tests
```

Or filter to a single test class:

```bash
dotnet test tests/Harbor.Tools.Builtin.Tests --filter "FullyQualifiedName~Tools"
```

## Layer

Tests — depends on the project(s) under test + TUnit (test framework). No production code.

## See also

- [../../docs/ARCHITECTURE_LAYERS.md](../../docs/ARCHITECTURE_LAYERS.md)
- [../../docs/DEVELOPMENT.md](../../docs/DEVELOPMENT.md)
