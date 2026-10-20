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
2. **`Microsoft.NET.Test.Sdk` is not used, and must not be.** It was declared in
   16 test projects and pinned in `Directory.Packages.props`; #1893 removed all
   of them. TUnit's installation docs say that package must not be used with
   TUnit because it stops test discovery. `tools/check-no-test-sdk.py` fails the
   build if it comes back.

Because of (2) the older blanket claim that `dotnet test` "discovers zero
tests repo-wide" never held as written — the package named as the blocker was
never in most of the test projects, and it is not in any of them now. What *is*
established is that no CI job has run `dotnet test` since it was abandoned, so
its current per-project behaviour is **unverified**. Running each project as a
plain executable is the form CI actually exercises, which is why it is the only
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
legacy lane (`GoldenFrameTests`, four scenarios) render a component in the
headless compositor and compare the CONTENT of the frame — the visual tree,
the text, the resolved colours, the font sizes — against a committed
baseline (`*.golden.txt`). A mismatch fails with the test name, the first
differing line, and the path of the written `.actual.txt` next to the
reference.

**The baselines are checked on CI, on every run.** The lane used to compare
SHA-256 hashes of PNG rasters, and every one of those comparisons was skipped
on a shared runner: a raster is a function of the machine's Skia build and
font stack, so a baseline captured on one host never matched another, and the
most fragile part of the UI was checked nowhere (#1795). A content snapshot
is a function of the code alone, so the same baseline holds on every host.

A change of the frame therefore fails the build unless the baseline was
regenerated in the SAME change — the same deliberate re-sign the public-API
snapshot gate asks for.

```bash
# Option A: regenerate locally (writes the *.golden.txt baselines)
HARBOR_UPDATE_GOLDENS=1 dotnet run --project tests/Harbor.E2E.App.Avalonia -c Release -- \
  --treenode-filter "/*/*/*Golden*/*"

# Option B: regenerate on CI without a local .NET setup (PR branch, not dev)
gh workflow run goldens.yml --ref <branch> -f suites="tests/Harbor.E2E.App.Avalonia"
```

Both options write the same files. Option B additionally re-runs the suites in
verify mode before committing, so a baseline the code does not reproduce is
not committed.

Review the `.actual.txt` diff, then commit the regenerated `*.golden.txt`
baselines in the same commit as the change that moved the frame. The
geometry of a text-bearing frame is NOT part of the baseline: the size of a
text control is a function of the host's font metrics (the app's mono slot
is a system font stack), so a baseline that carried the geometry would be
pinned to one machine's fonts. The layout is covered by the screenshot
artifacts the suite writes (`~/.harbor/test-screenshots-*`) and by the
structural assertions of the other UI suites.

## Skipped tests

A skipped test asserts nothing, so the number of them is published on every
CI run: the `test` job renders the skipped tests of each shard, with the
reason each one declared, into the job summary (`tools/test-skips.py`).

```bash
# the same view, from a local TRX set
python3 tools/test-skips.py TestResults/e2e
```

The house rule this serves: a skipped test needs a reason, and the count
must not grow silently. The Avalonia golden lane is the worked example — 20
of its 133 tests were skipped on every CI run (12 golden frames skipped
because a raster baseline never matches across machines, 7 `KnownFlake`
tests, 1 order-dependent settings test); the golden skips are gone, and the
rest are listed with their reasons on every run.
