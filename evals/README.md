# evals v0 — external task-solving baseline (no runtime changes)

External runner: fixture → Harbor CLI (`ask`, plain events) → timeout →
independent verifier → artifacts + verdict. See `docs/EVALS.md`.

```bash
# from repo root (build the CLI first):
dotnet build apps/Harbor.App.Cli -c Release
export KILO_API_KEY=klo_...
dotnet run --project tools/Harbor.Evals -c Release -- --tasks evals/tasks --profile evals/profiles/local.json --attempts 1
```

Results land in `evals/results/<batch>/` (gitignored). Batch summary:
`summary.json` + `SUMMARY.md`.

## Add a task

Copy `tasks/guard-divide/`, keep `task.json` schema v1, write `prompt.md`,
`verifier/verify.sh` (args: workspace, verificationOutput; exit 0 always on
a completed verification, checks JSON with pass/fail/inconclusive), and
`constraints.json`. Verifier must never depend on agent claims — only files.
