# Harbor.LoadTests

Concurrent multi-session load suite (issue #420): `sessions × agents`
agent runs through the REAL stack — shared `AgentLoop` singleton, shared
`InMemoryEventBus`, real OpenAI-compatible HTTP client over SSE against one
`MockLlmServer` in echo mode — on every storage backend.

## What's covered

- Scenario matrix (`MatrixShape`, `HeavyShape`): `1×1`, `2×1`, `4×2`,
  `10×3` by default; `50×1`, `100×1` under `HARBOR_LOAD=1`. Every shape
  asserts completion, no deadlock, intact persisted transcripts, and no
  cross-session `SessionId` bleed.
- Store backends (`StoreBackend_*`): the same concurrent pipelines run on
  `memory`, `jsonl` and `sqlite`, so write contention and file-handle
  pressure are covered. File-backed rows own an isolated temp path each.
- Rate limiter ladder (`RateLimiter_*`): capacity 1 strictly serializes
  (peak stays 1); capacity equal to the run count admits everything with
  no drops and no starvation.
- Event-bus ordering (`EventBus_*`): per-session turn streams strictly
  alternate start/end under interleaving, every finalized message was
  announced by id, no `TurnEndEvent` precedes its `TurnStartEvent`.
- Paired scaling gate (`Scaling_*`): N then 2N sessions in one run,
  `t(2N) <= 3 · t(N)` plus exact admission counts.

## Determinism

The only pacing is the refund-on-completion `TokenBucketRateLimiter` and
`MockLlmServer` chunk-delay time dilation — the harness never sleeps on
real time. Budgets are relative (same-run paired ratio) or
machine-independent counts, never absolute wall-clock thresholds (#996,
#998). The `CancellationTokenSource` bounds in the tests are liveness
tripwires (a deadlock must fail, not hang the lane), deliberately
generous, not perf gates.

## Run

Fast matrix only (default PR lane):

```bash
dotnet run --project tests/Harbor.LoadTests -c Release --no-build -- --minimum-expected-tests 1
```

Full matrix including the heavy shapes:

```bash
HARBOR_LOAD=1 dotnet run --project tests/Harbor.LoadTests -c Release --no-build -- --minimum-expected-tests 1
```

Or filter to a single class:

```bash
dotnet run --project tests/Harbor.LoadTests -c Release --no-build -- \
  --minimum-expected-tests 1 --treenode-filter "/*/*/MultiSessionLoadTests/*"
```

## Layer

Tests — depends on the project(s) under test + TUnit (test framework). No production code.

## See also

- [../../docs/E2E_TESTING.md](../../docs/E2E_TESTING.md)
- [../../docs/DEVELOPMENT.md](../../docs/DEVELOPMENT.md)
