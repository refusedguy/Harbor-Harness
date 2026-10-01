# Harbor.Abstractions.Tests

Tests for **Harbor.Abstractions**.

## What's covered

Domain contracts - Result<T>, Message, ToolCall, AgentEvent, Permission, SessionId

## Run

```bash
dotnet run --project tests/Harbor.Abstractions.Tests -c Release --no-build -- --minimum-expected-tests 1
```

Or filter to a single test class:

```bash
dotnet run --project tests/Harbor.Abstractions.Tests -c Release --no-build -- \
  --minimum-expected-tests 1 --treenode-filter "/*/*/Result/*"
```

## Layer

Tests — depends on the project(s) under test + TUnit (test framework). No production code.

## See also

- [../../docs/ARCHITECTURE_LAYERS.md](../../docs/ARCHITECTURE_LAYERS.md)
- [../../docs/DEVELOPMENT.md](../../docs/DEVELOPMENT.md)
