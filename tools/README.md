# tools/

Helper scripts and small .NET tools used during development. Nothing here ships
in the CLI binary — `apps/Harbor.App.Cli` has no dependency on this directory.

Two rules that keep this directory honest:

1. **Every script must be listed below.** An undocumented script is
   indistinguishable from a leftover from a sprint that has already merged.
2. **Every script documents itself in its header.** The table below is a map,
   not the manual — usage and exit codes are in the first ~20 lines of each
   file. Most take no `--help`; read the header.

## Shell scripts

| Script | What it does | Referenced from |
|---|---|---|
| [`pr-ops.sh`](./pr-ops.sh) | PR/CI one-liners for `gh` — status sweeps, failure extraction, log downloads, mergeability. Read-only except `pr-merge-if-green`. | [AGENTS.md](../AGENTS.md) §MCP preference |
| [`code-inspect.sh`](./code-inspect.sh) | Post-change gate: the minimal build + test set that must stay green after any UI/Avalonia/XAML change. | [docs/CI_UI_TESTS.md](../docs/CI_UI_TESTS.md) |
| [`ci-ui-tests.sh`](./ci-ui-tests.sh) | Scoped run of the four Avalonia test classes (component behaviour, view inflation, killer features, theme parity). Uses TUnit's `--treenode-filter`, **not** `--filter`. | self-documented; CI-UI-TESTS.md covers the classes |
| [`post-change-check.sh`](./post-change-check.sh) | Older sibling of `code-inspect.sh` — same intent, with a `--timeout` wrapper. Kept because it is what existing muscle memory reaches for. | self-documented |
| [`ui-hygiene.sh`](./ui-hygiene.sh) | Pre-build XAML gate: no bare class selectors, required `xmlns` on `MainWindow.axaml`, markdown renderer subscribed to theme changes. | [docs/CI_UI_TESTS.md](../docs/CI_UI_TESTS.md) |
| [`kimodule-rules.sh`](./kimodule-rules.sh) | Standalone XAML style-selector linter (`Selector="Border.Card"` ok, `Selector=".Card"` not). Separated from `ui-hygiene.sh` so it can run on any `.axaml` dir via `--axaml-dir`. | self-documented |
| [`create-harbor-issues.sh`](./create-harbor-issues.sh) | Bulk-creates the recommended issue set via `gh`. `--dry-run` prints without creating. | self-documented; one-shot bootstrap, not a recurring workflow |

## Python scripts

| Script | What it does | Referenced from |
|---|---|---|
| [`check-md-links.py`](./check-md-links.py) | Verifies every internal markdown link and GitHub anchor in the tracked `.md` files. Stdlib-only Python 3 — no pip, no network, no dotnet. Exit 0 clean / 1 broken / 2 no input. `--self-test` proves it still fails on a broken link. | this README, [docs/DEVELOPMENT.md](../docs/DEVELOPMENT.md) |
| [`md-lint.py`](./md-lint.py) | Markdown structural gate: encoding (LF, no BOM), heading levels, ATX spacing, fences, single trailing newline. Deliberately a small rule subset — the header lists the markdownlint rules it refuses to enforce and the measured hit count that ruled each one out. `--self-test` proves it still fails on bad input. | [docs/DEVELOPMENT.md](../docs/DEVELOPMENT.md) |
| [`md_gate.py`](./md_gate.py) | Shared plumbing for the two gates above: the tracked-file list, the vacuity guard (`--min-files`/`--min-refs`/`--min-lines` floors) and the throwaway-fixture harness their `--self-test` modes use. Not runnable on its own. | imported by both gates |
| [`msbuild-evaluation-cost.py`](./msbuild-evaluation-cost.py) | What a Release compile of this tree spends on MSBuild PROJECT EVALUATION, read from the performance summary MSBuild prints. A measurement, so it reports into the job summary and never fails a build. `--self-test` proves it still parses the summary into the numbers it says it does. | [docs/BENCHMARKS.md](../docs/BENCHMARKS.md), `.github/workflows/evaluation-cost.yml` |
| [`check-test-json-reflection.py`](./check-test-json-reflection.py) | Gates the SERIALIZER the test tree uses: does every `JsonSerializer` call under `tests/` name a source-generated type info, or does it take the reflection resolver? Stdlib-only Python 3 — no pip, no network, no dotnet. The product side of System.Text.Json already rides source generation (21 contexts under `src/`); the test side did not, and a reflection call in a test is a test that can stay green while the product is broken. Exceptions are named and justified in [`test-json-reflection-exceptions.txt`](./test-json-reflection-exceptions.txt), and the file is a ratchet — an exception the tree no longer produces fails the gate. `--self-test` proves it still fails on a planted reflection call. | [`docs/DEVELOPMENT.md`](../docs/DEVELOPMENT.md) §Reflection-JSON gate |
| [`check-test-packages.py`](./check-test-packages.py) | Gates the package set of the test and sample projects: which packages may each of them reference, directly or transitively, and is any banned one among them? Reads the `<PackageReference>` elements of the `.csproj` and the resolved package graph `dotnet restore` writes (`obj/project.assets.json`) — no build, no network. The ban set comes from `BannedSymbols.txt`, not from a second list. Exceptions are named and justified in [`test-package-exceptions.txt`](./test-package-exceptions.txt). `--self-test` proves it still fails on a banned package. | [`docs/DEVELOPMENT.md`](../docs/DEVELOPMENT.md) §Package-set checks, `AGENTS.md` §"What NOT to do" #2 |
| [`check-dup-jscpd.py`](./check-dup-jscpd.py) | The SECOND duplication detector (#1847): jscpd 5.4.1 (MIT, a self-contained Rust binary) over `src/` + `apps/` + `tests/`, with the config in [`.jscpd.json`](../.jscpd.json) and the committed snapshot in [`jscpd-baseline.json`](./jscpd-baseline.json). It finds the duplication that `check-test-dup.py` structurally cannot see — duplication that is not a method body, and duplication in the product code. It does NOT replace that gate: jscpd's own `--fail-on-new-clones` is keyed on a content hash, so an edit of an existing duplicate reads as new duplication (measured on 13 real commits), and the rule here is the aggregate one instead. The binary is downloaded from the release and sha256-pinned per platform; it is a build-time tool and no `PackageReference` is added. `--self-test` proves it still fails on an empty scan and on growth. | [`docs/DEVELOPMENT.md`](../docs/DEVELOPMENT.md) §The second duplication detector |
| [`test-measurements.py`](./test-measurements.py) | Renders the per-test measurements of a CI run into the job summary: every test that printed something, with the value, the class and (since #1884) the duration the runner measured. Reads the TRX artifacts the `test` job already produces, so it adds no run and no artifact. `--self-test` proves it still finds the row and reads the duration. | [docs/BENCHMARKS.md](../docs/BENCHMARKS.md) §6, `.github/workflows/ci.yml` |
| [`test-census.py`](./test-census.py) | Per-test and per-project WALL census of a CI run, from the same TRX artifacts: every result the runner produced (printed or not), so it answers "how many tests does this suite have" and "what does this project cost" with the discovered count, the sum of the per-test durations, the wall and the parallelism factor. `--format csv` / `--format tsv` emit every row for sorting. Stdlib only, no build, no network, no dotnet. `--self-test` proves it still counts a test that printed nothing. | [docs/BENCHMARKS.md](../docs/BENCHMARKS.md) §6.1, `.github/workflows/ci.yml` |
| [`gen-e2e-shards.py`](./gen-e2e-shards.py) | Generates the process plan of the headless Avalonia E2E suite (`tests/Harbor.E2E.App.Avalonia/e2e-shards.json`): the class universe comes from the source tree, the costs from a measured CI TRX, and the split is a balanced partition of the measured durations. Stdlib-only. `--print-universe` lists the classes it found. | [docs/TEST_PATTERNS.md](../docs/TEST_PATTERNS.md) §Parallelism |
| [`run-e2e-avalonia-shards.py`](./run-e2e-avalonia-shards.py) | Runs that plan: one `dotnet exec` per shard, in parallel, each with its own `--treenode-filter` and its own screenshot directory. Fails loudly when a shard's filter drops a class, and names the upstream Avalonia defect when a shard's process is poisoned. Called by the `test` job in CI. | [docs/TEST_PATTERNS.md](../docs/TEST_PATTERNS.md) §Parallelism |
| [`aot-publish-gate.py`](./aot-publish-gate.py) | The AOT publish gate: the publish's outcome and its diagnostic inventory must match the committed record in `.github/aot-warning-baseline.txt`, and the published binary must actually run. A gate FOR a build, so it sits outside it. `--selftest` proves it still fails on a planted diagnostic id. | [docs/AOT_PUBLISH.md](../docs/AOT_PUBLISH.md), `.github/workflows/ci.yml` |
| [`check-abstractions-contract.py`](./check-abstractions-contract.py) | The dependency surface of the four guarded `src/` projects: every declared package must be load-bearing, and every non-BCL namespace the sources use must be covered by a declaration. `--self-test` proves it still fails on a phantom package. | [docs/DEVELOPMENT.md](../docs/DEVELOPMENT.md), `AGENTS.md` §"What NOT to do" #1 |
| [`check-doc-cites.py`](./check-doc-cites.py) | The citations in the normative documents: a `file:line` must still point at a line, and a backticked type must still be constructed by something. The scope is the documents carrying the `Status: normative` banner. `--self-test` proves it still fails on a stale citation. | [docs/DEVELOPMENT.md](../docs/DEVELOPMENT.md) |
| [`check-doc-facts.py`](./check-doc-facts.py) | The repo-map counts in `AGENTS.md` and `CLAUDE.md` against the tree: a stated count must equal the count of tracked files it is counting. `--self-test` proves it still fails on a stale count. | [docs/DEVELOPMENT.md](../docs/DEVELOPMENT.md) |
| [`check-gate-policy.py`](./check-gate-policy.py) | The gate policy's own gate: the table in [docs/CI_GATES.md](../docs/CI_GATES.md) must still describe the python tools CI runs, with the self-tests, the ratchets and the floors the workflows really pass. `--self-test` plants one defect per rule and asserts that the rule fires. | [docs/CI_GATES.md](../docs/CI_GATES.md) |
| [`check-public-api-resign.py`](./check-public-api-resign.py) | The re-signing half of the public-API freeze: a change to a frozen `PublicAPI.Shipped.txt` has to be re-signed in the same commit. The other half is a Roslyn analyzer (RS0016/RS0017) in the build. `--self-test` proves it still fails on an unreviewed change. | [docs/ANALYZERS.md](../docs/ANALYZERS.md) |
| [`check-reference-trimmer.py`](./check-reference-trimmer.py) | Does any project in the whole tree reference a package or a sibling project the compiler never uses? The findings the tree has today are in `reference-trimmer-baseline.txt`, and the file can only shrink. `--self-test` proves it still fails on a dead reference, on a baseline row without a reason, and on a coverage count below the floor. | `.github/workflows/ci.yml` |
| [`check-test-dup.py`](./check-test-dup.py) | Duplicate test bodies under `tests/`, against the committed baseline in `test-dup-baseline.json`. Advisory while the removal programme is in flight (`--warn-only` in CI). `--self-test` proves it still fails on a planted duplicate. | [docs/DEVELOPMENT.md](../docs/DEVELOPMENT.md) §Test duplication gate |
| [`check-test-projects.py`](./check-test-projects.py) | Test-project completeness: every test project in the solution is discovered by a CI run, read from the shard lists in `ci.yml` and `test-os.yml`. The holes the tree has today are recorded in `test-project-baseline.txt`. `--self-test` proves it still fails on a project left out of CI. | [docs/DEVELOPMENT.md](../docs/DEVELOPMENT.md) |
| [`check-xml-doc-coverage.py`](./check-xml-doc-coverage.py) | XML-doc coverage of the frozen contract assembly: every public member needs a doc or a named, justified suppression. `--self-test` proves it still fails on an undocumented member. | [docs/XML_DOC_AUDIT.md](../docs/XML_DOC_AUDIT.md) |
| [`ci-shard-slices.py`](./ci-shard-slices.py) | The build-output slices must match the test shards: the same list is written twice, and a project in one and not in the other fails the build, naming the project. | `.github/workflows/ci.yml` |
| [`demo_repro.py`](./demo_repro.py) | The demo GIFs: the frame pacing is re-emitted onto a fixed cadence, every tape is recorded twice and the two passes are compared, and the manifest is validated. | [docs/DEMO_GIFS.md](../docs/DEMO_GIFS.md) |
| [`normalize-mtimes.py`](./normalize-mtimes.py) | After the build-output cache is restored, the source mtimes are set to the truth, so MSBuild rebuilds exactly what the pull request changed and skips the rest. `--self-test` proves it still tells a changed file from an unchanged one. | `.github/workflows/ci.yml` |
| [`pack-validate.py`](./pack-validate.py) | Every packed `Harbor.*` NuGet package carries its metadata, its license expression, its README and its `lib`. `--selftest` proves it still fails on a package without them. | `.github/workflows/ci.yml` |
| [`test-skips.py`](./test-skips.py) | The tests a run skipped, with the reason each one declared, in the job summary. It reads the TRX artifacts the run already produced, so it adds no run and no build. `--self-test` proves it still finds a skipped test. | `.github/workflows/test-os.yml` |

The policy these live under — why they are python, what the compiler takes
over instead, and what every gate owes — is
[docs/CI_GATES.md](../docs/CI_GATES.md), and `check-gate-policy.py` is the
gate over the table in it.

## .NET tools

| Project | What it does | Referenced from |
|---|---|---|
| [`Harbor.Evals/`](./Harbor.Evals) | Task-solving eval runner — executes `evals/tasks` fixtures against a live model and scores the result. Stdlib + provider HTTP only; no runtime changes to Harbor. | [AGENTS.md](../AGENTS.md) §Evals, [docs/EVALS.md](../docs/EVALS.md) |
| [`DesignSystemDocGen/`](./DesignSystemDocGen) | Generates the public theme API reference from the compiler-emitted XML doc of `Harbor.DesignSystem` + `docs/schemas/harbor-theme.schema.json`. Fails the build if a listed token is missing from the docs — deliberate: doc rot should not be silent. | [docs/DESIGN_SYSTEM_API.md](../docs/DESIGN_SYSTEM_API.md) |

## Note on the eval runner

`tools/Harbor.Evals` is an external consumer, not a library. It is listed in
`Harbor.slnx` so the IDE indexes it, but nothing in `apps/Harbor.App.Cli` or
`src/` references it — it may take a model API key, spawn processes and take
minutes, none of which belongs in the CLI's dependency graph. Protocol and
fixture format are documented in [docs/EVALS.md](../docs/EVALS.md).
`tools/DesignSystemDocGen` is not in the solution at all; it is run manually
when the theme reference needs regenerating.
