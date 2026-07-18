# Harbor.Core.Tests

Tests for **Harbor.Core + Harbor.Abstractions + Harbor.Storage.Memory**.

## What's covered

AgentLoop, EventBus, ToolRegistry, ProviderRegistry, compaction, system prompt builder

## Run

```bash
dotnet test tests/Harbor.Core.Tests
```

Or filter to a single test class:

```bash
dotnet test tests/Harbor.Core.Tests --filter "FullyQualifiedName~AgentLoop"
```

## Layer

Tests — depends on the project(s) under test + TUnit (test framework). No production code.

## See also

- [../../docs/ARCHITECTURE_LAYERS.md](../../docs/ARCHITECTURE_LAYERS.md)
- [../../docs/DEVELOPMENT.md](../../docs/DEVELOPMENT.md)
