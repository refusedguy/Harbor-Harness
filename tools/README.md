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
