# Harbor.Providers.Tests

Tests for **Harbor.Providers.* (Anthropic, OpenAI, Ollama, OpenAiCompatible)**.

## What's covered

HTTP request shape, message converter, streaming NDJSON / SSE parsing, error handling

## Run

```bash
dotnet run --project tests/Harbor.Providers.Tests -c Release --no-build -- --minimum-expected-tests 1
```

Or filter to a single test class:

```bash
dotnet run --project tests/Harbor.Providers.Tests -c Release --no-build -- \
  --minimum-expected-tests 1 --treenode-filter "/*/*/Provider/*"
```

## Layer

Tests — depends on the project(s) under test + TUnit (test framework). No production code.

## See also

- [../../docs/ARCHITECTURE_LAYERS.md](../../docs/ARCHITECTURE_LAYERS.md)
- [../../docs/DEVELOPMENT.md](../../docs/DEVELOPMENT.md)
