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
| [`check-test-json-reflection.py`](./check-test-json-reflection.py) | Gates the SERIALIZER the test tree uses: does every `JsonSerializer` call under `tests/` name a source-generated type info, or does it take the reflection resolver? Stdlib-only Python 3 — no pip, no network, no dotnet. The product side of System.Text.Json already rides source generation (21 contexts under `src/`); the test side did not, and a reflection call in a test is a test that can stay green while the product is broken. Exceptions are named and justified in [`test-json-reflection-exceptions.txt`](./test-json-reflection-exceptions.txt), and the file is a ratchet — an exception the tree no longer produces fails the gate. `--self-test` proves it still fails on a planted reflection call. | [`docs/DEVELOPMENT.md`](../docs/DEVELOPMENT.md) §Reflection-JSON gate |
| [`check-test-packages.py`](./check-test-packages.py) | Gates the package set of the test and sample projects: which packages may each of them reference, directly or transitively, and is any banned one among them? Reads the `<PackageReference>` elements of the `.csproj` and the resolved package graph `dotnet restore` writes (`obj/project.assets.json`) — no build, no network. The ban set comes from `BannedSymbols.txt`, not from a second list. Exceptions are named and justified in [`test-package-exceptions.txt`](./test-package-exceptions.txt). `--self-test` proves it still fails on a banned package. | [`docs/DEVELOPMENT.md`](../docs/DEVELOPMENT.md) §Package-set checks, `AGENTS.md` §"What NOT to do" #2 |
| [`test-measurements.py`](./test-measurements.py) | Renders the per-test measurements of a CI run into the job summary: every test that printed something, with the value, the class and (since #1884) the duration the runner measured. Reads the TRX artifacts the `test` job already produces, so it adds no run and no artifact. `--self-test` proves it still finds the row and reads the duration. | [docs/BENCHMARKS.md](../docs/BENCHMARKS.md) §6, `.github/workflows/ci.yml` |
| [`test-census.py`](./test-census.py) | Per-test and per-project WALL census of a CI run, from the same TRX artifacts: every result the runner produced (printed or not), so it answers "how many tests does this suite have" and "what does this project cost" with the discovered count, the sum of the per-test durations, the wall and the parallelism factor. `--format csv` / `--format tsv` emit every row for sorting. Stdlib only, no build, no network, no dotnet. `--self-test` proves it still counts a test that printed nothing. | [docs/BENCHMARKS.md](../docs/BENCHMARKS.md) §6.1, `.github/workflows/ci.yml` |
| [`gen-e2e-shards.py`](./gen-e2e-shards.py) | Generates the process plan of the headless Avalonia E2E suite (`tests/Harbor.E2E.App.Avalonia/e2e-shards.json`): the class universe comes from the source tree, the costs from a measured CI TRX, and the split is a balanced partition of the measured durations. Stdlib-only. `--print-universe` lists the classes it found. | [docs/TEST_PATTERNS.md](../docs/TEST_PATTERNS.md) §Parallelism |
| [`run-e2e-avalonia-shards.py`](./run-e2e-avalonia-shards.py) | Runs that plan: one `dotnet exec` per shard, in parallel, each with its own `--treenode-filter` and its own screenshot directory. Fails loudly when a shard's filter drops a class, and names the upstream Avalonia defect when a shard's process is poisoned. Called by the `test` job in CI. | [docs/TEST_PATTERNS.md](../docs/TEST_PATTERNS.md) §Parallelism |
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
