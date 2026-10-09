# Harbor.LoadTests — concurrent multi-session matrix (#420)

In-process load suite: the real agent stack (shared `AgentLoop` singleton,
shared `InMemoryEventBus`, real OpenAI-compatible HTTP client over SSE)
against one `MockLlmServer` in echo mode, with `sessionCount × agentsPerSession`
concurrent agent runs.

Files:

- [MultiSessionLoadTests.cs](MultiSessionLoadTests.cs) — the matrix itself.
- [MultiSessionLoadHarness.cs](MultiSessionLoadHarness.cs) — stack composition
  plus the `LoadStoreBackend` switch (`memory` / `jsonl` / `sqlite`).
- [LoadTestFakes.cs](LoadTestFakes.cs) — minimal composition fakes.

## Shape

Session shapes `1×1` and `10×3` run on every PR; `50×1` and `100×1` run only
under `HARBOR_LOAD=1`. The store axis repeats the small shape (`4×1`) on all
three backends every run, and the `50×1` shape on the file-backed backends
under `HARBOR_LOAD=1`. Every leg asserts the same invariants: all runs
complete, every agent start has a matching end (no EventBus deadlock), the
token bucket admits every run exactly once without exceeding capacity, the
persisted transcript alternates user/assistant with byte-exact echo hashes,
and every message and turn event carries its run's `SessionId`.

## Determinism

The only pacing is the refund-on-completion `TokenBucketRateLimiter` and the
mock server's chunk-delay dilation. The harness never sleeps on real time, so
results do not depend on core count or runner neighbours.

## Budgets are relative, never absolute

An absolute wall-clock threshold on a shared CI runner measures the runner,
not the code. The scaling gate therefore pairs 8 sessions against 4 inside one
run (`t(8) <= 3 · t(4)`, best-of-3 per leg, warm-up discarded), so the runner's
speed cancels. The `[Timeout]` values are liveness tripwires — a deadlock must
fail, not hang — not budgets. See also
[docs/E2E_TESTING.md](../../docs/E2E_TESTING.md).

## Run commands

```bash
# Fast shapes only (what PR CI runs)
dotnet run --project tests/Harbor.LoadTests -c Release -- --minimum-expected-tests 1

# Full matrix including the heavy shapes
HARBOR_LOAD=1 dotnet run --project tests/Harbor.LoadTests -c Release -- --minimum-expected-tests 1
```
