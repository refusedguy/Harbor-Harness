# evals v0 — external task-solving baseline (no runtime changes)

External runner: fixture → Harbor CLI (`ask`, plain events) → timeout →
independent verifier → artifacts + verdict. See `docs/EVALS.md`.
Related: #40, spec 17 §2.

```bash
# from repo root (build the CLI first):
dotnet build apps/Harbor.App.Cli -c Release
export KILO_API_KEY=klo_...
dotnet run --project tools/Harbor.Evals -c Release -- --tasks evals/tasks --profile evals/profiles/local.json --attempts 1
```

Results land in `evals/results/<batch>/` (gitignored). Batch summary:
`summary.json` + `SUMMARY.md` (aggregates + per-task table: verdict,
constraints, verify, exec, failure class, duration, cost).

## Seed set → spec 17 §2 scenarios

| Task | Spec §2 scenario |
|---|---|
| `py-fix-failing-test` | 1. small logic bug |
| `py-total-guard`, `guard-divide`, `py-cli-guard` | 2. null/empty edge |
| `fix-empty-input`, `json-edit-port` | 3. small API-adjacent change (localized edit) |
| `multi-file-todo` | 4. multi-file change by pattern |
| `shell-sorted-list` | 4/8. constrained generation (untouched inputs, exact output) |
| `keep-public-api` | 8. forbidden public-API change (signatures verified, not trusted) |
| `readonly-diagnosis` | 7. read-only diagnosis (no-change + artifact) |

Scenarios 5 (behavior-preserving refactor), 6 (broken build), 9 (work over
user changes), 10 (contradictory spec) and the separate runtime-resilience
set (cancellation, reconnect, crash, retries) are v1/follow-up scope.

## Add a task

Copy `tasks/guard-divide/`, keep `task.json` schema v1, write `prompt.md`,
`verifier/verify.sh` (args: workspace, verificationOutput; exit 0 always on
a completed verification, checks JSON with pass/fail/inconclusive), and
`constraints.json`. Verifier must never depend on agent claims — only files.

## Manual agent-claim annotation (v0)

`agentClaim` (`success | failure | unknown`) is set by a human, never parsed
from the transcript. After a batch finishes:

1. Read `results/<batch>/<task>/attempt-N/stdout.raw.log`.
2. Edit that attempt's `verdict.json`: set `agentClaim`, and `falseSuccess`
   (`true` only on explicit success claim + proven check failure).
3. Recompute the batch summary (no re-run):
   `dotnet run --project tools/Harbor.Evals -c Release --no-build -- --tasks evals/tasks --profile evals/profiles/local.json --summarize <batch>`.

## Live-run disclaimer

Live evals on a rotating endpoint measure Harbor together with the
provider's current routing policy. Results are diagnostic/descriptive;
single runs and small samples prove neither improvement nor non-regression.
Scripted runs check runtime on a controlled stream but do not replace
measuring the model's task-solving. Interleaved A/B (alternating versions
close in time, randomized order, no cherry-picking, model id recorded,
timeouts counted) only for behavioral changes; no automatic live
non-regression gate.

## Open (v1 / follow-ups, not v0)

- `usage.json` cost wiring (full cost to success stays `unknown` until then).
- Windows verifiers (`task.json → verifier.windows` is empty; Unix only).
- Short-lane timeouts (per-task 240 s, suite deadline 12 min) for live CI.
