# AOT publish gate

Owner: issue #413 (48/S2). The gate is the `aot-publish` job in
[`.github/workflows/ci.yml`](../.github/workflows/ci.yml); the instrument is
[`tools/aot-publish-gate.py`](../tools/aot-publish-gate.py); the committed
inventory it compares against is
[`.github/aot-warning-baseline.txt`](../.github/aot-warning-baseline.txt).

## The pass criterion

**"Zero IL2026 warnings" is not the pass criterion, and this repository does not
gate on it.** The criterion is three things, all of which must hold:

1. `dotnet publish -p:HarborWithAot=true` completes for `apps/Harbor.App.Cli`.
2. The trim/AOT diagnostics in that publish match
   [`.github/aot-warning-baseline.txt`](../.github/aot-warning-baseline.txt)
   exactly — no new id, no changed count, no new first-party site, and no row
   left behind for a warning that stopped.
3. The **published binary runs**: `--version`, `--help`, `--providers`, each
   asserted on content rather than on exit code.

All three are needed, and neither of the last two implies the other.

- A green publish does not prove the artifact boots. NativeAOT is a different
  program from the JIT one: statically linked, no JIT, no runtime assembly
  loading. Trimming can remove a method that only a reflective call site
  reaches, and the linker reports nothing when the call site is itself removed.
- A binary that boots does not mean anyone read the warnings. The inventory is
  the only record that the diagnostics were *considered*, and the verdict
  (`ACCEPTED` / `REJECTED`) is what makes that a decision rather than a count.

A gate that only counted warnings in a log nobody reads is a gate whose
threshold nobody chose. The inventory is committed and diffed in review for
exactly that reason.

## Why the published binary, and not just the publish

Before this gate, the repo's AOT claim was a **declaration that nothing
evaluated**:

- `IsAotCompatible=true` is set on four projects
  ([`Harbor.DesignSystem`](../src/Harbor.DesignSystem/Harbor.DesignSystem.csproj),
  [`Harbor.Ui.Framework.Rendering`](../src/Harbor.Ui.Framework.Rendering/Harbor.Ui.Framework.Rendering.csproj),
  [`Harbor.Tui.CellForge`](../src/Harbor.Tui.CellForge/Harbor.Tui.CellForge.csproj),
  [`Harbor.Tui.CellForge.Engine`](../src/Harbor.Tui.CellForge.Engine/Harbor.Tui.CellForge.Engine.csproj)).
  `apps/Harbor.App.Cli` — the one project with a real NativeAOT block — is
  not among them.
- No workflow anywhere in the repo set `HarborWithAot` or `HARBOR_MINIMAL`, so
  the `PropertyGroup` in
  [`Harbor.App.Cli.csproj`](../apps/Harbor.App.Cli/Harbor.App.Cli.csproj)
  conditioned on `HarborWithAot` was never read by any build.

The consequence is worth stating precisely, because it is stronger than "not
verified": the claim was **unfalsifiable**. There was no process whose output
could contradict it. `IsAotCompatible` is a declaration that can be removed
without anything going red, and the four rows in
[`AotBlockDemotionRules.KnownDemotions`](../tests/Harbor.Architecture.Tests/AotBlockDemotionRules.cs)
said "not triaged" for the honest reason that no build had ever emitted the
diagnostics they describe.

## The publish recipe, and why not `HARBOR_MINIMAL`

The CLI csproj documents **two** AOT recipes and they are not equivalent (#747):

| Recipe | What it does |
|---|---|
| `HARBOR_MINIMAL=true dotnet publish -c Release -r linux-x64` | Turns AOT on **and** forces `HarborWithPlugins`, `HarborWithSpectreTui`, `HarborWithAllProviders` and `HarborWithAllTools` to `false`. Produces the stripped binary. |
| `dotnet publish -c Release -r linux-x64 -p:HarborWithAot=true` | Turns `PublishAot` on and **nothing else**. The four feature flags keep their `true` defaults, so `Harbor.Plugins.Compilation` (Roslyn) stays in the compile. |

**The gate uses the second.** The minimal profile publishes a different, smaller
program than the one that ships, so its inventory would not describe the
shipping artifact, and its `--providers` output would be a single provider —
which would gut the one smoke assertion that proves build-time resource
embedding survived. Whether recipe 2 *should* also strip the features is a
design question, not a gate question; it belongs to #48/S4, which owns the
Roslyn plugin-host decision. This gate takes no position on it.

Note that the NUKE `PublishAot` target
([`build/_build/Components/PublishVariantBuilder.cs`](../build/_build/Components/PublishVariantBuilder.cs))
sets the raw MSBuild property `PublishAot=true` rather than `HarborWithAot`, so
it does **not** evaluate the csproj's AOT block at all. That is a separate
observation and is not this gate's business; it is recorded here because it
means the block has exactly one evaluator, and it is the one this job drives.

## How the block actually behaves — two stages, asymmetrically

Measured by reading the targets files shipped with SDK 10.0.302 and
`Microsoft.DotNet.ILCompiler` 10.0.10, not inferred:

| Stage | Warnings-as-errors in force | `WarningsNotAsErrors` honoured | New IDs fail the publish? |
|---|---|---|---|
| ILLink | **no** — `ILLinkTreatWarningsAsErrors=false` is set by the block | yes (`Microsoft.NET.ILLink.targets`) | **no** — caught only by the inventory |
| ILC | **yes** — `IlcTreatWarningsAsErrors` defaults to `$(TreatWarningsAsErrors)`, which is `true` repo-wide, and the block does not set it | yes (`--nowarnaserr`, `Microsoft.NETCore.Native.targets`) | **yes** |

So the four demoted ids (`IL2026`, `IL3050`, `IL2104`, `IL3053`) are printed and
not fatal, and any *other* ILC diagnostic fails the publish outright. The
ILLink stage is the softer one, and the inventory is the only net over it. This
asymmetry is the reason the inventory exists rather than a bare
"publish succeeded" check, and it is why the baseline records exact counts
rather than a boolean.

### One property in the block is inert

`NativeAOTErrorOnWarningsAsErrors=false` is **not read** by SDK 10.0.302 or by
`Microsoft.DotNet.ILCompiler` 10.0.10. `grep -rn NativeAOTErrorOnWarningsAsErrors`
over both target sets returns nothing. The real property is
`IlcTreatWarningsAsErrors`, which defaults to `$(TreatWarningsAsErrors)`.

This is recorded rather than fixed here. Changing it would change what fails the
publish, and the honest way to make that change is with a measured inventory in
hand and a deliberate decision about which stage should be strict. It is
`ILLinkTreatWarningsAsErrors` and the demotion list that currently govern the
build, and the demotion list is the one that is load-bearing.

## Inventory granularity, and why there are no line numbers

A row is `<ID> | <count> | <verdict> | <first-party files> | <reason>`.

- **Counts are exact, not thresholds.** A change in either direction fails.
  A row for a diagnostic that no longer occurs fails too — delete it in the same
  diff, or it has become a permission for a concession nobody is making.
- **Line numbers are deliberately excluded.** A baseline keyed on `file:line`
  has to be edited by every unrelated change to the lines above a warning, and
  a baseline that churns on every commit is a baseline contributors delete. The
  actionable and stable granularity is the first-party **file**: a warning that
  moves to a new file is a real change; a warning whose line moved by three is
  not. The exact sites, with line numbers, are in the `aot-publish-log`
  artifact, so a reviewer can read them without the baseline carrying them.
- **Third-party sites are counted, not listed.** A package's internals move with
  its version and are not actionable per site. The `files` column is `-` when
  every site is outside `src/` and `apps/`.
- **`REJECTED` is a bug, not a concession.** A `REJECTED` row that is still
  emitted **fails** the gate, because it means the fix has not landed.
- **`IL0xxx` is out of scope.** Compiler diagnostics are errors already via
  `TreatWarningsAsErrors`, and a publish that emitted one never reached the
  tool.

## No wall-clock threshold — and that is a decision, not an omission

There is deliberately **no** time threshold on this publish. #998 measured a
**1.43× spread on a single commit** (2.167s–3.088s across six green runs) on a
step far cheaper than an ILC pass. An absolute wall-clock gate here would fire
on a slow runner and pass on a fast one, which is a false signal in both
directions. `timeout-minutes: 45` is a hang detector, not a performance gate.

If a duration trend is ever wanted, the right shape is a recorded measurement in
[`docs/BENCHMARKS.md`](BENCHMARKS.md) alongside several runs, not a threshold
in a job.

## What the gate cannot go green on

`tools/aot-publish-gate.py` returns **exit 0** and **exit 2** as different
values, and exit 2 is not a softened exit 0:

| Exit | Meaning |
|---|---|
| 0 | the publish reported success **and** the inventory matches the committed rows |
| 1 | the gate ran and said no — drift, or a baseline row that would not survive review |
| 2 | the gate could not run — no log, log under the byte floor, or **no publish sentinel** |

The sentinel (`HARBOR_AOT_PUBLISH_OK rid=<rid>`) is written by the job only
after `dotnet publish` exits 0, and the tool also requires the sentinel's `rid` to
match `--expect-rid` so a different publish's log cannot be substituted. This is
the #988 shape — a shard that reported success having run nothing — applied to a
job that publishes rather than to one that runs tests, and the form is identical.

The tool also prints the id list and occurrence count on **every** run, so
`found 0` can never be a bare number in a log (#901 reported "Found 0" from a
matcher that had been matching nothing).

`--selftest` runs 22 fixtures where the outcome is guaranteed by construction —
a planted extra id, a planted extra *occurrence* of an id already in the
baseline (the #591 "the tool halved the rule" shape), a planted new first-party
site, a missing sentinel, a wrong rid, a truncated log, an absent log, and 7
malformed-baseline rows. Each assertion checks the exit code **and** the
substance of the message, so a matcher that fails for the wrong reason cannot
pass. The CI job runs it before publishing, so a broken instrument is caught in
about a second rather than after a 20-minute ILC pass.

## Where this gate does not run, and why that is correct

- **Docs-only PRs.** `ci.yml` lists `**.md` and `docs/**` in `paths-ignore`, so
  no `ci.yml` job runs on a docs-only PR — including this one. That is intended:
  a publish has nothing to do with a paragraph, and the reason `docs.yml` exists
  as a separate workflow is the same reason (#509). Do not "fix" it by removing
  those entries.
- **Edits to the gate's own files ARE covered.** `tools/aot-publish-gate.py` and
  `.github/aot-warning-baseline.txt` are both listed in the `changes` job's
  `dotnet` filter. Without that, editing the instrument — or the baseline, which
  is the ordinary way to answer a red AOT job — would set `dotnet=false`, skip
  the very job that runs it, and land unvalidated. That is the #618 defect with
  a different payload.
- **`workflow_dispatch` is not relied on.** The default branch is `master`, and a
  workflow that exists only on `dev` is not dispatchable (`gh workflow run` → 404,
  #924). This gate rides `push` and `pull_request` on `dev`, the same mechanism
  every other job in `ci.yml` uses, so it has exactly the same reachability. A
  dispatch-only trigger would have reproduced #924.

## Deliberately out of scope

- **The `ask` smoke.** #413's acceptance criteria ask for "a scripted `ask`
  against the deterministic fake client from #48/S1". **#411 (S1) has not
  landed** and there is no fake client in the tree. The three verbs that need no
  provider key are implemented; the gap is named here rather than dropped
  silently.
- **Removing the demotion list.** The csproj demotes four ids via
  `WarningsNotAsErrors`; those warnings are printed, not gone, and the inventory
  is the record of them. Emptying the list is a separate decision that wants the
  first inventory in hand.
- **`Harbor.Plugins.Host` (#1005).** That project is not in `Harbor.slnx` and is
  referenced by nothing, so the "AOT core + JIT host" construction does not
  build. This gate neither touches nor breaks it: the publish closure is
  `Harbor.App.Cli` → `Harbor.Hosting` → the plugin *layers*, and
  `Harbor.Plugins.Host` is in neither. The `IL3050` the inventory carries comes
  from `Harbor.Plugins.Compilation`, which *is* referenced. Coupling the two
  would make this gate depend on an unlanded issue.
- **A repo-wide `IsAotCompatible` sweep.** Four projects carry the flag today and
  this gate says nothing about which projects should. That is #48/S3's
  trimming-safety audit.
