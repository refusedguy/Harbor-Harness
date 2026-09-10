# EVALS — task-solving quality (contour B)

Correctness tests (contour A) prove Harbor honors its contracts. Evals prove
it solves tasks. Different loops, different gates. Related: #40, spec 17 §2.

## v0 runner (`tools/Harbor.Evals`, external, no runtime changes)

Per attempt layout: `results/<batch>/<task>/attempt-N/{workspace,verifier,artifacts}`.

1. Copy fixture → `workspace`; capture initial manifest (path → sha256).
2. Copy `verifier/` aside (never inside workspace).
3. Run Harbor: `dotnet <Cli.dll> ask` with piped prompt, `cwd = workspace`,
   kill process tree on timeout (grace: none in v0 — kill is the boundary).
4. Capture raw stdout/stderr; parse `[marker]` events to `events.jsonl`
   (unknown lines → `parser-diagnostics.json`, never silently dropped).
5. Capture final manifest; run verifier (own timeout).
6. Write `attempt.json` (ids, commit, provider/model, env NAMES only —
   never values/secrets, UTC + durations, exit code, runner version),
   `verification.json`, `verdict.json`, `changes.json`.

## Verdict (never one enum)

- execution: `completed | timed_out | crashed | harness_error`
  (`agent_end` + exit 0 = protocol complete, NOT task solved).
- verification: `pass | fail | error` (exit 0 = verifier ran; checks may fail;
  missing/bad JSON = error).
- task: `solved | failed | inconclusive`.
- constraints: `pass | fail | not_evaluated` (manifest-compared outside:
  `mustRemainUnchanged`, `mustNotCreate`; never trust agent self-report).
- agentClaim: manual in v0 (`success | failure | unknown`); no regex on
  «готово». `falseSuccess` only when explicit success claim + proven failure.

## Metrics (batch summary)

Solved share (timeouts count in denominator; harness errors shown
separately); constraint-pass share + coverage; false-success share;
**full cost to success** (all attempts / successes; unknown cost = null,
never zero); failure classes (`incorrect_change`, `broke_existing`,
`violated_constraint`, `timeout`, `harness_or_crash`, `unknown`).

## Rules

- Verifier prompt never gets verifier feedback (no runner-as-fixer loop).
- Conditions pinned per batch: Harbor commit, provider/model, fixture
  versions, limits, date. Real models are nondeterministic — v0 is
  diagnostic (3 tasks fine), not statistical proof.
- Live-model runs are a signal, not a gate; deterministic scripted-provider
  E2E stays the engineering gate.
- v1 plugs the same tasks into RunId/budgets/recovery when Run exists.
