# ddmin shrink contract for UI-sequence failures

Shrinking a flaky UI-sequence failure is a bounded mechanical step instead of
manual bisection. This document states the agreed contract; the implementation
lives in `tests/Harbor.Tui.E2E.Tests/Shrink/` and the tests assert every clause
below. Runner integration is out of scope for this slice — the shrinker only
shrinks; some other piece drives it.

## Order of tools: fixed lists first

The default path is the fixed cancel, approve, commit list, in that order. The
randomized generator is the overlay, not the starting point, and it is opt-in:
the same seed always yields the same sequence, and the generator version is
recorded with every artifact.

## Determinism gate

Before any reduction, the full sequence replays twice under the same schedule,
and both replays must fail with the same failure signature. Shrinking without
a controlled schedule shrinks noise: a flaky case reduced without the gate can
come out passing, which launders a real flake into a false pass.

A run whose gate fails refuses to start. The refusal is reported as a
non-determinism finding about the sequence, not as a shrink result.

## Budgets

- `MaxShrinkChecks = 50`: at most fifty predicate evaluations per run, gate
  replays included.
- `MaxShrinkDuration = 30 s`: at most thirty seconds per run, measured from
  the start of the run.

Either bound stops the reduction loop.

## Exhaustion behavior

A run that hits either bound stops and returns `BestKnownFailingCase` — the
shortest failing case found so far, still reproducing the gated signature
under the same schedule. Exhaustion never yields a passing case, and there is
no verdict that reports the run itself as a failure.

## Failure artifact

A failure carries all five fields: the seed, the full sequence, the generator
version, the failure signature, and the commit sha. The seed alone is
insufficient to reproduce a run, so an artifact with an empty generator
version is rejected rather than recorded.

## Schedule control

The timing and interleaving of a sequence are controlled by the harness, not
by wall-clock luck. Step delays derive from the seed, the run replays them on
an injected clock, and the shrinker hands the same schedule object to every
check. Two replays with the same seed observe identical timestamps.

## Time-box enforcement and residual risk

The duration bound is enforced from the start of the run, not only between
checks: every check receives a token carrying the remaining run budget, so one
hung cooperative check cannot blow the thirty-second budget. Residual risk,
stated openly: a check that ignores its token overruns anyway. The harness
must only hand in cooperative checks; the bound cannot redeem a check that
never yields.

## Wording

Reports and documents use exactly this claim:

```text
no further reduction by chosen transforms
```

The claim describes what the chosen transforms achieved. It is never
strengthened into a statement that no shorter failing case exists.

## Pointers

- [E2E test README](../tests/Harbor.Tui.E2E.Tests/README.md)
- [DdminShrinker](../tests/Harbor.Tui.E2E.Tests/Shrink/DdminShrinker.cs)
- [Shrinker tests](../tests/Harbor.Tui.E2E.Tests/Shrink/DdminShrinkerTests.cs)
- [Failure artifact](../tests/Harbor.Tui.E2E.Tests/Shrink/FailureArtifact.cs)
- [Schedule and clocks](../tests/Harbor.Tui.E2E.Tests/Shrink/UiSequenceSchedule.cs)
