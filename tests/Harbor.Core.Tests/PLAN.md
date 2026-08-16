# Plan — Harbor.Core.Tests

## Status: MVP

## Done

- [x] Unit tests for core public API
- [x] TUnit test framework wired in
- [x] CI green

## TODO

- [ ] Increase coverage to >=85% on public surface
- [ ] Add property-based tests for edge cases
- [ ] Add integration tests against real provider/storage backends (skipped in CI)

## Known issues

- Some tests depend on filesystem temp dirs - flaky on heavily loaded CI runners.

## Next priorities

1. **P1**: Snapshot tests for serialization round-trips
2. **P2**: Mutation testing with Stryker
