# README staleness audit (issue #430)

**Date:** 2026-09-28
**Base commit:** `de2358f` (branch `dev`)
**Scope:** every `README.md` under `src/` (50 files: 48 in `*.csproj` directories
plus `Harbor.Providers.Shared` and `Harbor.Storage.Shared`, which are
linked-source folders with no `.csproj`).
**Method:** mechanical, greppable checks — not eyeballing. Four probes, each
described below with its false-positive rate.

## Result in one line

Coverage went **48/54 → 54/54**; **4 files corrected**; **1 code-level
follow-up** filed; **0 READMEs deleted or renamed**.

## What was reviewed

All 50 `src/**/README.md` files. Every one was run through all four probes
below; the 6 that had no README were written from the code.

## The four probes

| # | Probe | Result |
|---|-------|--------|
| 1 | `file:line` citations — does the file exist, and is the line in range? | 19 citations, **19 valid** |
| 2 | `HARBOR_*` / `*_API_KEY` env vars cited vs. the 26 defined in code | 4 cited, **4 valid** |
| 3 | `ProjectReference` claims in each Dependencies section vs. the `.csproj` | **4 real defects** (below) |
| 4 | `HARBOR_TUI=<id>` claims vs. the ids in `TuiBackendRegistry` | **1 real defect** (below) |

### Probe notes / false positives

Probe 3 produced 30 raw hits, of which **26 were false positives** worth
recording so the next auditor does not re-litigate them:

- `Harbor.Abstractions` and `Harbor.Extensions` *cite* projects they
  deliberately do **not** reference ("`Harbor.Extensions` is deliberately *not*
  referenced", "**Zero Harbor project references**"). Correct as written.
- `Harbor.Desktop.Abstractions` lists `Harbor.Core` under **Forbidden**, and
  its "Allowed" line already covers all 8 real references. Correct as written.
- `Harbor.Application`, `Harbor.Registries` name sibling projects in prose about
  *layer rules*, not as references. Correct as written.
- `Harbor.Hosting` does not enumerate its 36 references. Deliberate — it is the
  composition root; a 36-row table would rot faster than it informs.

A naive probe 5 (every backticked PascalCase identifier must be a declared
type) was **abandoned**: 415 hits, essentially all noise — project names
(`Harbor.Core`), member names (`Lines`, `Push`), package names (`Serilog`,
`MemoryPack`) and BCL types. Type-name verification is therefore a *manual*
audit activity, not something the CI gate attempts. The gate checks structure;
it does not pretend to check semantics.

## Corrections made

### 1. `src/Harbor.Tui.Notifications/README.md` — claimed activation path does not exist

Documented `HARBOR_TUI=notifications` / `tui: "notifications"` as the way to
select the renderer, twice.

**Verified false:** `notifications` is not in `TuiBackendRegistry`'s backend
ids (`ansi`, `cellforge`, `fullscreen`, `nickconsoleex`, `plain`, `razor`,
`spectre`, `spectre-tui`, `termina`, `terminal-gui`) and the only alias is
`consoleex`. `NotificationTuiRenderer` carries no `[TuiRenderer(Backend = …)]`
attribute, and **`src/Harbor.Hosting` has no `ProjectReference` to this
project at all** — the only consumers are `tests/Harbor.Architecture.Tests` and
`tests/Harbor.Tui.RendererTests`.

Corrected to state the renderer is not selectable, why, and how it is actually
driven. The claim was corrected rather than deleted, because "the code exists
but is not wired" is the more useful fact.

### 2. `src/Harbor.Desktop.Animations/README.md` — wrong project reference

"✅ **Allowed**: `Harbor.Desktop.Abstractions` (for `RgbColor`)."

**Verified false:** the csproj's only `ProjectReference` is
`Harbor.DesignSystem`. `RgbColor` is declared in
`src/Harbor.DesignSystem/DesignSystem/RgbColor.cs` and moved there in the
issue #33 split; the `Harbor.Desktop.Abstractions` edge is gone. Corrected,
with a note recording what changed and when.

### 3. `src/Harbor.DesignSystem/README.md` — dead repository URL, stale renderer names

Linked to `https://github.com/harbor-sh/harbor`, a repository that does not
exist. Repointed to `https://github.com/refusedguy/Harbor-Harness`, matching
`RepositoryUrl` in `Directory.Build.props` (also corrected in this PR).
Separately, the "one design system, every renderer" line still advertised
"ConsoleEx" as a current backend; `consoleex` is a legacy **alias** for
`cellforge`, and the `Harbor.Tui.Ansi` / `Harbor.Tui.Plain` projects it also
implied were merged into `Harbor.Tui.AnsiPlain`. Updated to name the renderers
that exist.

### 4. `src/Harbor.Ui.Framework.State/README.md` — omitted a real reference

The project table listed `Harbor.Abstractions` and
`Harbor.Ui.Framework.Abstractions` but not `Harbor.Ui.Framework.Rendering`,
which the csproj does reference. That edge is the issue #33 T1 key-vocabulary
one (`KeyEventAdapter` over the BCL-only `UiKeyDto`), so its absence was
actively misleading about the layering. Added.

## Verified correct, left alone

Recording these so a future audit does not re-open them:

- All 19 `file:line` citations across the 50 READMEs resolve, and every cited
  line is in range.
- Every env var cited in a `src/` README (`HARBOR_TUI`, `HARBOR_STORAGE`,
  `HARBOR_MCP_CONFIG`, `HARBOR_HOME`) is read by real code.
- `Harbor.Abstractions` and `Harbor.Extensions` zero-dependency invariants are
  stated correctly, and both are enforced by
  `AbstractionsSplitLayerRules`.
- `Harbor.Tui.CellForge` and `Harbor.Tui.AnsiPlain` correctly document the
  `HARBOR_TUI` values and the `consoleex` legacy alias.

## Filed as follow-up (code, not docs — out of #430's scope)

**`apps/Harbor.App.Cli/Repl/SlashCommandDispatcher.cs:367`** offers
`"notifications"` in the `/tui` completion list. Given finding #1, that
completion can never resolve to a renderer — the user types `/tui
notifications` and silently gets `ansi`. This is a user-facing bug in code, not
a documentation defect, so it is not fixed here. It should be removed in the
same change that either wires the notifications backend or drops it.

## Not done, and why

Enforcing the full six-section template on all 43 packable projects would mean
rewriting 36 existing READMEs, and every added usage snippet would be an
unverified claim — precisely what #430's acceptance criteria forbid
("public type and method names cited in the snippet exist … not a promise").
Doing that without builds is how the current drift was created.

Instead the 36 are recorded in `LegacyNonConformantReadmes` in
`ReadmeCoverageTests.cs`, each mapped to its missing sections, and the list is
a ratchet: a test fails if an entry overstates what is missing, or names a
project that is gone. It can only shrink. New projects are held to the full
template from day one.
