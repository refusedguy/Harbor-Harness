# AOT publish gate

Owner: issue #413 (48/S2). The gate is the `aot-publish` job in
[`.github/workflows/ci.yml`](../.github/workflows/ci.yml); the instrument is
[`tools/aot-publish-gate.py`](../tools/aot-publish-gate.py); the committed
inventory it compares against is
[`.github/aot-warning-baseline.txt`](../.github/aot-warning-baseline.txt).

## The pass criterion

**"Zero IL2026 warnings" is not the pass criterion, and this repository does not
gate on it.** The criterion is two things, both of which must hold:

1. The publish's **outcome and diagnostic inventory** match
   [`.github/aot-warning-baseline.txt`](../.github/aot-warning-baseline.txt)
   exactly — the same recorded outcome, no new id, no changed count, no new
   first-party site, and no row left behind for a warning that stopped.
2. The **published artifact** is checked. **Today this half is inactive, and
   saying so is part of the criterion rather than a footnote**: a failing ILC
   publish emits no binary (measured — an earlier run of this job reached
   `no executable at publish/aot/Harbor.App.Cli`), so while the record says the
   publish fails there is nothing to run. The step checks the record's claim in
   both directions — a `failed` publish must have produced *no* artifact, a
   `published` one must have produced one that runs `--version` / `--help` /
   `--providers` on content — so it stays a real assertion instead of a skip.
   The transition is forced by the outcome check below; see [the ratchet
   section](#this-gate-is-a-ratchet-not-a-green-light--and-that-is-the-whole-design).

Neither implies the other. A publish that completes does not prove the artifact
boots — NativeAOT is a different program from the JIT one, statically linked,
no JIT, no runtime assembly loading, and trimming can remove a method that only
a reflective call site reaches. And an artifact that boots says nothing about
whether anyone read the warnings: the inventory is the only record that the
diagnostics were *considered*, and the verdict (`ACCEPTED` / `REJECTED`) is what
makes that a decision rather than a count.

## This gate is a ratchet, not a green light — and that is the whole design

**No AOT publish configuration in this repository works today.** That is
measured, and it is the reason the gate is shaped the way it is:

- recipe `fulltree` (`-p:HarborWithAot=true`) reaches ILC and **fails** on
  three ids the csproj does not demote;
- recipe `minimal` (`HARBOR_MINIMAL=true`) **does not compile**.

So the publish step is `continue-on-error`, and the gate step immediately after
decides. The committed record says *"this publish fails, like this"*, and the
gate passes **only** on that:

| What changed | Verdict |
|---|---|
| nothing | pass |
| the publish starts **succeeding** | **red** — "the AOT state improved, update the record" |
| a new id, changed count, or new first-party site | **red** |
| a recorded id stops appearing | **red** — delete or promote the row |
| no marker in the log at all | **red**, exit **2** — there was no publish |

### Two things about this gate are weaker than they look, stated here rather
### than left for the next reader to discover

**The "run the published binary" half is currently inactive.** A failing ILC
publish emits no binary, so there is nothing to execute. The step still runs and
still asserts — that a `failed` publish left no artifact behind — but it does not
demonstrate executability, and no amount of reading the job's green tick will
change that. It activates on its own the moment the publish starts succeeding:
the outcome check goes red first, the record is updated, and the branch flips.
What is missing from the current state is the thing #413 asked for, and the
honest description of this PR is "the gate that will hold the AOT state still",
not "the AOT state is good".

**The three published-binary assertions have never executed against a real
artifact.** They are the part of this gate that has not been shown to work. The
first run in which the publish succeeds is the first real test of them, and if
they are wrong the fix will land in the same PR that fixes the AOT build.

That last row is the one that matters most. A publish that produced no log, a
truncated log, or a log from the other recipe is not "the recorded state"; it is
the absence of evidence, and the gate reports it as a different exit code rather
than as a pass.

`continue-on-error` is safe here *because* the next step is a real check against
a committed expectation. It is not a way to make a failure quiet. If the
publish step dies before writing its marker, the gate says exit 2 and the upload
step fails on `if-no-files-found: error`.

**What this gate is not.** It does not certify that Harbor is AOT-clean, and it
is not a claim that the four demoted ids are the right four. It is a pinned,
reviewed record of the AOT state plus a tripwire on that record changing. When
issue #48/S4 resolves the plugin path, this gate goes red and the record is
updated in the same diff — which is the transition it exists to force.


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

## The publish recipe, and the two ways it was broken

The CLI csproj documents **two** AOT recipes and they are not equivalent (#747):

| Recipe | What it does |
|---|---|
| `minimal` — `HARBOR_MINIMAL=true dotnet publish …` | Turns AOT on **and** forces `HarborWithPlugins`, `HarborWithSpectreTui`, `HarborWithAllProviders` and `HarborWithAllTools` to `false`. The csproj calls it *"the only path that produces the stripped binary, and the only one that matches what NativeAOT is supposed to mean here"*. |
| `fulltree` — `dotnet publish … -p:HarborWithAot=true` | Turns `PublishAot` on and **nothing else**. The four feature flags keep their `true` defaults, so the plugin projects and Roslyn stay in the compile. |

**This gate publishes `fulltree`, and both recipes were broken when it was
written.** Three separate defects, found by running them:

### 1. Neither recipe was invocable at all — `NETSDK1102`

```
error NETSDK1102: Optimizing assemblies for size is not supported for the
selected publish configuration. Please ensure that you are publishing a
self-contained app.
```

`Directory.Build.props` sets `SelfContained=false` repo-wide, the AOT
`PropertyGroup` never overrode it, and
`Microsoft.NET.ILLink.targets:214` is exactly
`<NETSdkError Condition="'$(SelfContained)' != 'true'" ResourceName="ILLinkNotSupportedError" />`.
The publish failed **before ILC ever ran**, on every invocation, since the day
the recipe was written. Fixed: the csproj now sets `SelfContained=true` inside
the AOT block, and the job passes the flag on the command line too so the gate
does not depend on that file staying shaped this way.

### 2. `fulltree` reaches ILC and fails

```
ILC : error IL3000: Harbor.Plugins.Compilation.PluginAssemblyReferences.
      BuildReferences(): 'System.Reflection.Assembly.Location.get' always returns
      an empty string for assemblies embedded in a single-file app
ILC : error IL3000: Harbor.Plugins.Compilation.PluginAssemblyReferences.
      EnsureReference(List`1<MetadataReference>,HashSet`1<String>,Assembly): [same]
ILC : error IL3000: Microsoft.CodeAnalysis.CommonCompiler.GetAssemblyLocation(Type): [same]
ILC : Trim analysis error IL2072: Harbor.Plugins.Instantiation.
      ReflectionPluginInstantiator.Instantiate(CompiledPluginAssembly): 'type'
      argument does not satisfy 'DynamicallyAccessedMemberTypes.
      PublicParameterlessConstructor' in call to 'System.Activator.CreateInstance'
ILC : Trim analysis error IL2070: Harbor.Plugins.Instantiation.
      ReflectionPluginInstantiator.<>c.<FindPluginTypes>b__1_1(Type): 'this'
      argument does not satisfy 'PublicParameterlessConstructor' in call to
      'System.Type.GetConstructor(Type[])'
error MSB3077: ilc … errors were detected during execution
```

The measured inventory, which is what
[`.github/aot-warning-baseline.txt`](../.github/aot-warning-baseline.txt)
records row by row:

| ID | count | demoted by the csproj? | effect |
|---|---|---|---|
| IL2026 | 14 | yes | printed, did not fail |
| IL3050 | 9 | yes | printed, did not fail |
| IL2104 | 3 | yes | printed, did not fail |
| IL3053 | 2 | yes | printed, did not fail |
| **IL3000** | **4** | **no** | **error — fails the publish** |
| **IL2072** | **1** | **no** | **error — fails the publish** |
| **IL2070** | **1** | **no** | **error — fails the publish** |

Three findings fall out of that table.

**The counts are a property of the tree, not of the diagnostic family.** Setting
`SelfContained=true` in the AOT block (fix 1 above) moved IL2026 from 15 to 14,
IL3050 from 10 to 9, and IL3000 from 3 to 4 — it added a site
(`PluginAssemblyReferences.ResolveDeploymentDirectory`) and dropped
`JsonAppConfigStore` entirely. Nothing about the AOT analysis "changed
character"; the trim graph did, because the publish is now self-contained. This
is the reason the record is re-derived from a log produced by the *current* tree
rather than from an earlier one, and the reason an SDK bump may require
re-triage.

**The csproj's demotion list is incomplete: it names four ids and the publish
emits seven.** `AotBlockDemotionRules.KnownDemotions` said "not triaged" for its
four rows, and the honest answer to "what else is there" is three more.

**All three failing sites are in the plugin projects** —
`Harbor.Plugins.Compilation` and `Harbor.Plugins.Instantiation`. That is
exactly the surface #48/S4 has to decide about, and it is why the gate publishes
`fulltree`: the stripped profile would have hidden all of it behind an absence.

### 3. `minimal` does not compile — 11 × CS0234

```
apps/Harbor.App.Cli/Repl/ReplRunner.cs(83,37): error CS0234: The type or
namespace name 'PluginReloadService' does not exist in the namespace
'Harbor.Hosting'
… 11 occurrences across ReplRunner.cs, CellForgeReplRunner.cs,
SlashCommandDispatcher.cs, IReplHost.cs, PluginsPanelCommand.cs
```

[`PluginReloadService.cs`](../src/Harbor.Hosting/Modules/PluginReloadService.cs)
is **entirely** inside `#if HARBOR_WITH_PLUGINS`, and those 11 call sites
reference it with no guard. So `HarborWithPlugins=false` has never compiled, for
any project combination — which means the recipe the csproj calls the real AOT
path has never been evaluable either.

Fixing that means deciding what the REPL's plugin surface does when plugins are
absent, which is a behavioural question in `apps/Harbor.App.Cli` and not this
gate's to answer. Recorded, not fixed.

### Net

**The AOT state of this repository, as measured, is: the tree is not AOT-clean
with plugins compiled in, and the stripped profile cannot be built at all.** That
is a sharper statement than "unknown", and it is #48/S4's to resolve — the
`Microsoft.CodeAnalysis` IL3000 is not fixable from our code, and #413's own
criteria forbid buying green with blanket suppression.

`recipe=` is part of the publish marker, and the gate rejects a log from the
other recipe: the two build different programs, so an inventory from one
silently does not describe the other.

Note also that the NUKE `PublishAot` target
([`build/_build/Components/PublishVariantBuilder.cs`](../build/_build/Components/PublishVariantBuilder.cs))
sets the raw MSBuild property `PublishAot=true` rather than `HarborWithAot`, so
it does **not** evaluate the csproj's AOT block at all — and it would hit the
same `NETSDK1102`. Recorded because it means the csproj block has exactly one
evaluator, and it is the one this job drives.


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

A row is `<ID> | <severity> | <count> | <verdict> | <first-party sites> | <reason>`.

- **Severity is part of the key.** The same id as a warning and as an error are
  two rows; otherwise an error could hide behind a warning's count.
- **Counts are exact, not thresholds.** A change in either direction fails. A row
  for a diagnostic that no longer occurs fails too — delete it in the same diff,
  or it has become a permission for a concession nobody is making.
- **A "site" is a Harbor symbol, not a file path.** ILC and ILLink print a type
  and a method and *no source file*, and the only real path in an ILC log is
  Roslyn's own `src/Compilers/…`, which is third-party. A site may include a
  method name where the tool named one; it is deliberately not trimmed, because
  the two line shapes are indistinguishable — ILC writes `Type.Method(args):`
  where a compiler line writes `Type(line,col):` — and trimming wrong records a
  site that does not exist.
- **Line numbers are excluded.** A baseline keyed on `file:line` has to be
  edited by every unrelated change to the lines above a warning, and a baseline
  that churns on every commit is a baseline contributors delete. The exact sites
  are in the `aot-publish-log` artifact.
- **Third-party sites are counted, not listed.** A package's internals move with
  its version and are not actionable per site. The column is `-` when no site is
  first-party — which is the correct answer for `IL2104` and `IL3053`, which are
  per-assembly rollups.
- **`REJECTED` means "a defect that ought to be fixed", and it is expected to
  still be there.** A `REJECTED` row that stops appearing is an **improvement**
  and the gate goes **red** on it, demanding the row be promoted or deleted. The
  pass on a `REJECTED` row is printed as a `NOTE`, not swallowed: the reader is
  told what they are passing *on*.
- **`IL0xxx` and ordinary analyzer ids are out of scope.** `CS****` is in scope
  only because the stripped recipe currently fails to compile; when that is
  fixed the `CS` rows go away and the gate says so.

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
| 0 | the publish reported the recorded outcome **and** the inventory matches the committed rows |
| 1 | the gate ran and said no — a different outcome, diagnostic drift, or a row that no longer corresponds to anything |
| 2 | the gate could not run — no log, log under the byte floor, **no publish marker**, or a log from a different rid/recipe |

The sentinel (`HARBOR_AOT_PUBLISH_DONE rid=<rid> recipe=<recipe>
status=<published|failed> exit=<n>`) is written by the job when the publish step
reaches its end, **whatever the outcome** — because "the publish failed" is a
fact this gate has to be able to read. What it must never be able to do is read
nothing: the tool requires the marker to exist, requires its `rid` to match
`--expect-rid` and its `recipe` to match `--expect-recipe` (so neither another
platform's log nor the other AOT recipe's log can be substituted), and requires
the reported `status` to match `--expect-status`.

That last check is what makes the gate a ratchet rather than a rubber stamp. A
`status` mismatch is deliberately **exit 1, not exit 2**: the gate ran, read a
real publish, and found the world different from the record. "The gate could not
run" and "the AOT state changed" are different facts and get different codes.

This is the #988 shape — a shard that reported success having run nothing —
applied to a job that publishes rather than to one that runs tests, and the form
is identical.

The tool also prints the id list and occurrence count on **every** run, so
`found 0` can never be a bare number in a log (#901 reported "Found 0" from a
matcher that had been matching nothing).

`--selftest` runs 25 fixtures where the outcome is guaranteed by construction —
a planted extra id, a planted extra *occurrence* of an id already in the
baseline (the #591 "the tool halved the rule" shape), the same id arriving as an
error when the record has it as a warning, a planted new first-party site, MSBuild's
project tag not becoming a site, a missing marker, a wrong rid, a wrong recipe, an
**unexpected publish success**, a truncated log, an absent log, and 8
malformed-baseline rows. Each assertion checks the exit code **and** the
substance of the message, so a matcher that fails for the wrong reason cannot
pass. The CI job runs it before publishing, so a broken instrument is caught in
about a second rather than after a 20-minute ILC pass.

The project-tag case is there because it actually bit: MSBuild appends
`[…/apps/Harbor.App.Cli/Harbor.App.Cli.csproj]`, which contains the string
`Harbor.App.Cli`, and before that case existed the gate recorded the CLI as a
first-party site on every per-assembly rollup. Ten mutations of the comparison
logic — dropping the marker requirement, the status check, the count comparison,
the `NEW` branch, the `GONE` branch, the site capture, the project-tag strip, the
byte floor, the severity part of the key, and letting the informational notes
leak into the exit code — were each confirmed to turn the self-test red.

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

## What the `providers` assertion really proves

Not what it looks like. The obvious explanation — "the provider catalogue is
embedded, so this proves embedding survived the publish" — is **false**, and the
code says so:

- `EmbedProviders=true` is set on
  [`Harbor.Application`](../src/Harbor.Application/Harbor.Application.csproj) and
  [`App.Cli`](../apps/Harbor.App.Cli/Harbor.App.Cli.csproj), and
  [`Directory.Build.targets`](../Directory.Build.targets) turns that into an
  `EmbeddedResource` on whichever project carries the flag.
- `JsonProviderDiscovery.LoadEmbeddedProviders()` enumerates
  `typeof(JsonProviderDiscovery).Assembly` — which is **`Harbor.Hosting`** — and
  `Harbor.Hosting` does not set `EmbedProviders`. That enumeration is therefore
  empty, and the embedded-provider branch is dead as written.
- The catalogue actually reaches the registry through
  `ProviderPresetCatalog.FindProvidersDirectories()`, which yields
  `~/.harbor/providers`, then `<exeDir>/providers`, then up to 8 ancestors of
  `AppContext.BaseDirectory`. From `publish/aot/` that walk reaches the repo's
  `providers/*.json`.

So the assertion proves the AOT binary can locate, parse and register the
bundled JSON catalogue — that the `System.Text.Json` reading path and the
provider construction path both work under NativeAOT. That is worth having, and
it is a real risk area (the provider wire payloads are the place this repo
already hand-writes JSON over `Utf8JsonWriter`). It is **not** evidence about
build-time resource embedding.

The assertion also asserts that all four ids are present, which is derived from
the recipe: under `fulltree`, `HarborWithAllProviders` stays `true`, so
`ProviderFactories` registers the native Anthropic and OpenAI factories on top
of the Ollama one, and the JSON catalogue supplies `kilocode`. Under the
stripped recipe `anthropic` and `openai` would legitimately disappear — the
recipe section above says which recipe this job uses and why.

The dead embedded path is a pre-existing defect, recorded here and **not
fixed by this gate**: repairing it changes which providers a published binary
sees, which is a behavioural change well outside "add a gate". It belongs to
whoever owns provider discovery.

## Deliberately out of scope

- **Making the publish succeed.** See the recipe section: `fulltree` fails in
  ILC on three ids and `minimal` does not compile. Fixing either is a
  behavioural decision about the plugin path, which is #48/S4's, and #413's
  criteria forbid buying green with suppression.
- **The `ask` smoke.** #413's acceptance criteria ask for "a scripted `ask`
  against the deterministic fake client from #48/S1". **#411 (S1) has not
  landed** and there is no fake client in the tree. The three verbs that need no
  provider key are implemented; the gap is named here rather than dropped
  silently.
- **Removing the demotion list.** The csproj demotes four ids via
  `WarningsNotAsErrors`; those warnings are printed, not gone, and the inventory
  is the record of them. Emptying the list is a separate decision, and it now
  has the counts it needed to be made.
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
