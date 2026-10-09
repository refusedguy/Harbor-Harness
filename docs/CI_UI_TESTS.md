# CI — Normalized UI Test Runs

This document describes the canonical commands for running Harbor UI tests
locally and in CI pipelines.

## Test project layout

| Project | Scope |
|---|---|
| `tests/Harbor.App.Avalonia.Tests` | Headless Avalonia unit / inflation / parity tests |
| `tests/Harbor.E2E.App.Avalonia` | Full E2E UI tests (require real display / headless driver) |

## Categories

UI tests use TUnit `[Category]` attributes for filtering:

- `E2E` — end-to-end Avalonia UI scenarios.
- `Component` — granular component tests (toast, status-bar, chat, session list, onboarding, command palette, settings).
- `UI` — reserved for future headless-only UI unit tests.

## Local runs

Test projects are **plain executables** here — run them with `dotnet run
--project`, not `dotnet test` (see [Why not `dotnet test`](#why-not-dotnet-test)).

```bash
# All Avalonia headless tests (fast, no display required)
dotnet run --project tests/Harbor.App.Avalonia.Tests -c Release --no-build -- \
  --minimum-expected-tests 1

# View inflation only (headless)
dotnet run --project tests/Harbor.App.Avalonia.Tests -c Release --no-build -- \
  --minimum-expected-tests 1 --treenode-filter "/*/*/ViewInflationTests/*"

# E2E Avalonia tests (require Avalonia.Headless)
dotnet run --project tests/Harbor.E2E.App.Avalonia -c Release --no-build -- \
  --minimum-expected-tests 1 --treenode-filter "/*/*/*/*[Category=E2E]"

# Component subset of E2E (AND of both categories)
dotnet run --project tests/Harbor.E2E.App.Avalonia -c Release --no-build -- \
  --minimum-expected-tests 1 --treenode-filter "/*/*/*/*[Category=E2E][Category=Component]"
```

> **Note:** everything after `--` is forwarded to the test host, so the
> `--minimum-expected-tests` guard belongs there. TUnit filtering is
> `--treenode-filter`; the VSTest-style `--filter "Category=E2E"` is not
> supported under the MTP host. Category filters use the property syntax
> `[Category=X]` inside a treenode expression.

## CI pipeline (normalized)

`ci.yml` does not shell out to `dotnet test`; it executes the built test
assemblies. The equivalent block for the Avalonia UI suites:

```yaml
- name: Build
  run: dotnet build Harbor.slnx -c Release -nologo -clp:NoSummary

- name: UI Headless Tests
  run: dotnet run --project tests/Harbor.App.Avalonia.Tests -c Release --no-build -- \
        --minimum-expected-tests 1

- name: UI E2E Tests
  run: dotnet run --project tests/Harbor.E2E.App.Avalonia -c Release --no-build -- \
        --minimum-expected-tests 1 --treenode-filter "/*/*/*/*[Category=E2E]"
```

## Test report output

The host emits **TRX**, which is what the CI jobs collect — there is no JUnit
XML writer on this runner (see [Why not `dotnet test`](#why-not-dotnet-test)):

```bash
dotnet run --project tests/Harbor.App.Avalonia.Tests -c Release --no-build -- \
  --minimum-expected-tests 1 \
  --report-trx --results-directory artifacts --report-trx-filename avalonia-tests.trx
```

`ci.yml` feeds the TRX to `tools/test-measurements.py`, which renders a
`Class.Method -> measurement` table into the job summary.

## Why not `dotnet test`

`global.json` selects `Microsoft.Testing.Platform` as the `dotnet test`
runner, which changes what that command accepts. Two separate things break
here, and the older "discovers zero tests, exit 5" wording conflated them:

1. **VSTest-era options are rejected.** `--logger` and `--filter` are VSTest
   options; under the MTP runner they are forwarded to the test host, which
   rejects them. That is the platform's *invalid command-line arguments* exit
   code **5** — whereas a run that genuinely discovers no tests exits **8**
   (Microsoft Learn, *MTP troubleshooting* → Exit codes). The `Zero tests
   ran` / exit 5 failures in `renderer-perf-gate.yml` were this, and
   `CHANGELOG.md` (sprint *ci-cd-maturity*) records that deleting the single
   `--logger` flag turned that job green.
2. **11 of the 36 test projects still reference `Microsoft.NET.Test.Sdk`**
   (pinned in `Directory.Packages.props:71`). TUnit's installation docs say
   that package must not be used with TUnit because it stops test discovery.
   `TUNIT_MTP_AUDIT.md` proposes removing it; that change has not landed.

Because of (2) the older blanket claim that `dotnet test` "discovers zero
tests repo-wide" cannot hold as written — 25 of the 36 test projects never
reference the package named as the blocker. What *is* established is that no
CI job has run `dotnet test` since it was abandoned, so its current
per-project behaviour is **unverified**. Running each project as a plain
executable is the form CI actually exercises, which is why it is the only
form documented here.

## Pre-build hygiene gate

Run before building in CI to catch XAML style regressions:

```bash
bash ./tools/ui-hygiene.sh
```

## Demo GIFs

The four README GIFs are recorded by the `demo-gifs` workflow, not by the UI test
suites: they are VHS tapes driving `harbor --demo` with an in-process mock LLM, so
they need no credentials and no display. The workflow gates the recording against
`assets/demo/baseline.json` and commits the result back on a push to `dev`/`master`;
it deliberately does not run on pull requests.

Regenerating one by hand, telling an intended re-record from a foreign one, and
reading the drift gate's verdicts are all documented in
[DEMO_GIFS.md](DEMO_GIFS.md).

## Post-change inspection

After any XAML / style / theme change, run:

```bash
bash ./tools/code-inspect.sh
```

This builds the solution, runs architecture tests, and runs the full
`Harbor.App.Avalonia.Tests` suite.

## Accepting an intended golden change

The frames lane (`ComponentGoldenFramesTests`, eight scenarios) and the
legacy lane (`GoldenFrameTests`, four scenarios) compare settled headless
frames against committed baselines. A mismatch fails with the test name,
the expected hash, the actual hash, and the path of the written
`.verified.png` next to the reference — eyeball that diff before accepting.

```bash
# Option A: regenerate locally (writes PNGs + sha256.json manifest together)
HARBOR_UPDATE_GOLDENS=1 dotnet run --project tests/Harbor.E2E.App.Avalonia -c Release -- \
  --treenode-filter "/*/*/*Golden*/*"

# Option B: regenerate on CI without a local .NET setup (PR branch, not dev)
gh workflow run goldens.yml --ref <branch> -f suites="tests/Harbor.E2E.App.Avalonia"
```

Review the `.verified.png` diff, then commit the baseline PNGs and the
`ComponentTests/baselines/sha256.json` manifest in one commit. Pixel goldens
are skipped on shared CI runners (`SkipGoldenOnCi`) because the Skia and font
stack differs per host; enforce them on a pinned machine with
`HARBOR_GOLDENS_STRICT=1`.
