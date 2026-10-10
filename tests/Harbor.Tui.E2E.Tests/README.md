# Harbor.Tui.E2E.Tests

Tests for **Harbor.Tui.* renderers**.

## What's covered

End-to-end TUI smoke tests - feed a scripted agent event stream, capture rendered output, assert key bytes present

## Run

```bash
dotnet run --project tests/Harbor.Tui.E2E.Tests -c Release --no-build -- --minimum-expected-tests 1
```

Or filter to a single test class:

```bash
dotnet run --project tests/Harbor.Tui.E2E.Tests -c Release --no-build -- \
  --minimum-expected-tests 1 --treenode-filter "/*/*/E2E/*"
```

## Layer

Tests — depends on the project(s) under test + TUnit (test framework). No production code.

## UI-sequence shrink contract (#424)

Debugging a flaky UI sequence is a bounded mechanical step: the fixed
cancel-approve-commit list is the default path, the randomized generator is
opt-in, and the ddmin shrinker reduces a gated failure under a fixed schedule
with a 50-check / 30-second budget. The full contract, including exhaustion
behavior, the five-field failure artifact, and the reduction wording, is in
[docs/DDMIN_SHRINK_CONTRACT.md](../../docs/DDMIN_SHRINK_CONTRACT.md);
the implementation and its tests live in `Shrink/`.

## See also

- [../../docs/ARCHITECTURE_LAYERS.md](../../docs/ARCHITECTURE_LAYERS.md)
- [../../docs/DEVELOPMENT.md](../../docs/DEVELOPMENT.md)
