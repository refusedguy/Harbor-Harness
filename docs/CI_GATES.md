# CI gates: the policy for the python tools in `tools/`

This document says three things:

1. why the gates in `tools/` are python and not C#,
2. what the compiler takes over, and what stays in python because the compiler
   cannot see it,
3. what every gate owes the repository, and what a gate must not do.

The last one is not prose with nothing behind it. The gate table below is
checked by [`tools/check-gate-policy.py`](../tools/check-gate-policy.py), which
reads the table, the scripts in `tools/` and the workflow files, and fails when
they disagree. A policy that only says "remember to update the table" decays
into a table that describes a repository which no longer exists.

## The measured picture

Measured on this branch (`dev` at `ec2d38e9` plus this branch's commits),
the tree the table was last reconciled against:

| | |
|---|---|
| python tools in `tools/` | 35 files, 30 553 lines |
| of them, the `check-*.py` family | 17 files, 19 055 lines |
| workflows that run a python tool | 4: `ci.yml`, `demo.yml`, `docs.yml`, `nightly.yml` |
| gates that block a build | 29 |
| gates that report and let the build pass | 1 (`check-test-dup.py`, `--warn-only` while the removal programme runs) |
| controls for compile-time gates | 1 (`check-tunit-analyzer-gate.py`) |
| reporters, helpers, libraries | 9 reporters, 8 helpers, 3 libraries |
| tools with a self-test | 42 |
| tools with a ratchet | 21 |
| tools with a floor | 26 |
| tools whose self-test no workflow ran before this change | 3 (`pack-validate.py`, `normalize-mtimes.py`, and `test-skips`, which this change compiles) |
| gates in a workflow that runs a script that is not in the tree | 0 |

The last two rows are the two gaps this policy closed on the way in, and both
were found by the checker rather than by reading: the checker was run against
the tree before the table was written, and it named them. The last row is now
0: `ci.yml`'s `public-api` job runs the compiled `check-public-api-resign` gate and
`tools/public-api-nonvacuity.sh`, both of which are in the tree (the job that
used to name the removed `check-public-api-abstractions.py` is the `public-api
freeze` one, and it was re-wired).

Two more gaps, found the same way and worth naming.

The policy was written against a base that had already moved, and the rebased
tree carried python tools the first draft of the table did not know about
(`check-reference-trimmer.py` and `msbuild-evaluation-cost.py` on the first
move, `check-dup-jscpd.py`, `check-no-test-sdk.py` and
`run-e2e-avalonia-shards.py` on the second). The checker named each of them
on its first run against the new base, which is the whole argument for having
it.

The second move also found something worse: a squash that took the wrong side
of a conflict in `ci.yml` had dropped TWO gate jobs. The `reference-trimmer`
job and the `test-sdk-absent` job were gone from the workflow, so two gates
the tree still carried were not running at all. The checker named both: one
as `POLICY-MODE-GATE-NO-RUN`, the other as a python tool that no workflow
runs. A gate that is not wired cannot fire, and a gate that cannot fire is
what this policy is about.

## Why a gate is python or a committed binary

The reason is not speed. All the gates together take seconds, and moving
them to C# would add a restore, a build and a publish before each one.

The reason is **where the gate has to run**.

`ci.yml` ignores `**.md` and `docs/**` on purpose, for both `push` and
`pull_request`. A documentation-only pull request therefore never starts the
.NET build, and it gets a second workflow, `docs.yml`, which is deliberately
SDK-free. If those checks were C#, the check of the documentation of a pull
request that fixes the build would need the build to work first, and the
build is exactly what that pull request may have broken. The dependency would
be circular.

The same argument holds for the gates that run inside `ci.yml`: they read
files the build produced (a TRX, a publish log, a `.nupkg`) and files the
restore produced (`obj/project.assets.json`). They do not build anything. A
gate that needs the SDK cannot run on the pull request that fixes the build,
and a gate that needs a built Harbor cannot run on a documentation pull
request at all.

So a gate in this repository is a program that runs where Harbor does not
exist: no CLI, no product assemblies, no product dependencies, no NuGet
restore.

**A gate is one of two things**, and the difference is where its
implementation lives, not what it checks:

- a **python script** over files and artifacts: the standard library of a
  language every runner has, with nothing to build and nothing to install;
- a **compiled gate** of this repository: one multi-call binary
  (`harbor-gates <gate> [flags]`), one directory of Go sources per gate
  under `tools/gates/`, and the binary committed to the tree so that it
  runs where there is no SDK, no build of Harbor, and no python.

The reason for the second form is the first reason, sharpened: a gate has to
run where there is no SDK and no build, **and it must not cost the runner
minutes**. A committed binary satisfies both. The price is that a reviewer
cannot read a binary, and the answer to that is a machine check: the
`gates-binary` workflow rebuilds the binary from the sources in the same
tree and compares the bytes with the committed ones, so "the binary is what
the sources say" is a fact and not a promise.

Three reasons, and only three, put a check in `tools/` at all:

1. **It must run without the SDK and without a successful build.** The check
   has to work on the pull request that breaks the build.
2. **The invariant is a text or a tree, not a program.** A link, an anchor, a
   `file:line`, a markdown shape, a `.csproj`, a CI shard list, a TRX: all of
   them are files, and a file is read with a file API.
3. **It reads an artifact something else produced.** The test results, the
   publish log, the packed NuGet, the restore graph. The gate is a judge of an
   artifact, not a producer of one.

If none of the three holds, the check is a C# tool or a Roslyn analyzer, and
it belongs in the build, not in `tools/`.

## What is not done in python

Anything that needs a type, a symbol, or a resolved semantic model. A text
scan over source code cannot prove a property of the program it describes,
and the gates that try are the ones that cry wolf.

The public-API freeze is the worked example, and it is a decision rather than
a complaint about python.

**What it was.** Two python text scanners, 1212 lines between them:
`tools/check-public-api.py` for the contract assembly and
`tools/check-public-api-abstractions.py` for the facade. They read the source
text and matched member declarations against a committed baseline.

**What it is.** `Microsoft.CodeAnalysis.PublicApiAnalyzers` 5.6.0, attached to
the two frozen assemblies only, with `PrivateAssets=all`. `RS0016` (a public
symbol that the shipped file does not declare) and `RS0017` (a declared symbol
the code no longer has) fail the ordinary `build` job of `ci.yml`. The freeze
now lives where the freeze was always supposed to live: in the compile.

**Why the swap is not a downgrade.** The analyzer reads the compiler's symbol
table, so it sees what a line scan cannot:

- nullability (`RS0036` / `RS0037` / `RS0041`): `string` and `string?` are
  different signatures to the consumer, and a text scan saw only the word
  `string`;
- optional parameters (`RS0026` / `RS0027`), the compatibility rule that
  matters most when a surface is being frozen;
- a constructor that makes an inherited base `protected` (`RS0022`);
- implicit constructors and property accessors, which have no `public`
  keyword in the source at all;
- `const` and enum values, generic arity, and `init` accessors, all of which
  are part of a signature.

The inventory of analyzers and the decisions about them live in
[docs/ANALYZERS.md](ANALYZERS.md).

**What stayed in python, and why.** Half of the freeze cannot be a compile
gate: a change to the frozen surface has to be re-signed in the same commit,
and that is a question about a history, not about a tree. A compiler sees one
tree. So the re-signing rule is the compiled `check-public-api-resign` gate
([`tools/gates/check-public-api-resign/main.go`](../tools/gates/check-public-api-resign/main.go)),
and it is in the table below.

**How a compile-time gate proves it is not blind.** A python gate proves that
with `--self-test`. A compiler analyzer has no flag to run, so its
non-vacuity proof is a job:
[`tools/public-api-nonvacuity.sh`](../tools/public-api-nonvacuity.sh) builds a
throwaway project against the real analyzer and requires the build to fail
with `error RS0016`, then to pass once the row the analyzer asked for is
added, then to fail with `error RS0017` when the member is removed. The
lesson generalises: a gate that cannot be shown to fail is not a gate, and
how you show it depends on what the gate is made of.

## The rule of choice

Ask what the invariant needs:

| The invariant needs | The gate is |
|---|---|
| a link, an anchor, a heading shape, a `file:line` | a python text gate |
| a file tree, a `.csproj`, a CI list, a TRX, a baseline | a python file gate |
| the git history | a python gate over git plumbing |
| a resolved package graph | a python gate over `obj/project.assets.json` (a file) |
| a type, a symbol, a resolved overload set, a compiler's notion of visibility | a Roslyn analyzer, in the build |
| a duration | a benchmark, in [docs/BENCHMARKS.md](BENCHMARKS.md) |

The tie is broken by the question, not by the language the author is
comfortable in. A gate that asks "does this symbol exist?" is a compiler
question, and answering it with a regex is how a gate ends up reporting
findings that are not defects.

## What every gate owes

1. **A self-test with a planted control.** A gate that cannot fire is
   indistinguishable from a gate that is silent. The control is a real
   defect, and the self-test asserts both that the gate fails on it and what
   the gate says about it. A self-test that only asserts "exit code 1" is
   half a self-test: the message is the part a reader needs.
2. **A ratchet, and a floor that is not loosened.** Two different numbers,
   two different rules. A **baseline** (an exceptions file) is a ratchet: its
   rows may only shrink, because every row is a violation the tree is allowed
   to keep, and a new one is a new finding. A **floor** (`--min-*`) is the
   non-vacuity proof in numeric form: a scan that quietly narrowed must go red
   instead of reporting a clean tree. A floor is set AT the measured count,
   it is lowered in the same diff as the change that legitimately lowered the
   number, where a reviewer sees it, and it is RAISED when the measured count
   grows - a higher floor is stricter, and stricter is not a hole. What a
   floor may never do is be loosened to hide a regression.
3. **A deterministic exit.** The same tree gives the same exit code. No
   clock, no network, no randomness, no dependence on the machine.
4. **A message that names the violator.** "3 problems" costs the reader the
   whole run; the path, the line and the member cost nothing.
5. **No allow-list of files to skip, and no `continue-on-error`.** A skipped
   set is invisible at review time, and a gate that cannot fail is
   decoration.
6. **A header that says why it is python.** One line, near the top, in the
   form `Stdlib only - no pip, no network, no dotnet.` A **compiled** gate
   says why it is compiled instead, and its sources live under
   `tools/gates/<gate>/`, one directory per gate, with the standard library
   only and no external module.
7. **A compiled gate owes the repository its source, its bytes and its
   name.** The source is in `tools/gates/<gate>/`, the binaries are in
   `tools/bin/harbor-gates-linux-x64` and `tools/bin/harbor-gates-linux-arm64`,
   the hashes are in `tools/bin/SHA256SUMS`, and the name of the subcommand
   is the name of the directory. A pull request that changes one of them
   without the others is red, and the message names what to rebuild. There
   is never a python original and a compiled twin of the same gate in the
   tree: the twin is how a drifted pair is caught.

## What a gate must not do

- **Measure time.** A time threshold is a benchmark, and a benchmark belongs
  in [docs/BENCHMARKS.md](BENCHMARKS.md) with a methodology and a provenance.
  A gate that fails on a slow runner is a gate that gets switched off, and
  then it protects nothing.
- **Duplicate a gate that already exists.** Each of the ten `check-*.py`
  tools answers a different question, and each states its question in its
  header so the next author can see the shape of what is already covered.
- **Require dotnet.** See the reason the gates are python at all.
- **Claim to be a gate while running advisory.** One gate in this repository
  is advisory, and the table says so: `check-test-dup.py` runs with
  `--warn-only` while the duplication removal programme is in flight.

## The gate table

Every python tool in `tools/` (37 files), plus the COMPILED gates, whose
sources are in `tools/gates/<gate>/` and whose binary is committed to the
tree. 44 tools are run by a workflow; the other seven are
`gen-e2e-shards.py`, a generator a human runs, `md_gate.py`,
`string-switch-rewrites.py` and `tool_schema_model.py`, which are loaded by
other tools rather than run, `codemod-test-events.py`, `codemod-tool-schemas.py`
and `codemod-tool-names.py`, which rewrite hand-built sources. All of them are listed, because a tool that
is not listed is a tool nobody
reviews. The table records, for each one, the question
it answers, where its implementation lives, where it runs, whether it blocks a
build, whether it has a
self-test, the ratchet it holds, and the floors CI passes it. The table is
checked by `tools/check-gate-policy.py`, which fails when it stops describing
the tree: it names a tool the table does not list, and it names a table row
the tree does not have.

The modes are:

- **gate** - it fails a build.
- **advisory** - it reports and lets the build pass (`--warn-only`).
- **control** - it proves that a COMPILE-time gate is armed, so it runs the
  compiler. There are two of those: this one for the TUnit analyzer
  promotion, and `tools/public-api-nonvacuity.sh` for the public-API freeze.
- **reporter** - it prints and never fails.
- **helper** - a tool the pipeline uses, not a check of its own.
- **library** - imported by other tools, run by no workflow.

| Script | Implementation | What it checks | Runs in | Jobs | Mode | Self-test | Baseline | Floors |
|---|---|---|---|---|---|---|---|---|
| `tools/aot-publish-gate.py` | python | the AOT publish's outcome and diagnostics against the committed record | ci.yml | aot-publish | gate | `--selftest` | `.github/aot-warning-baseline.txt` | — |
| `tools/check-arch-anchors.py` | python | may an architecture rule anchor on a LINE NUMBER: a `file:line` is a coordinate, and every line inserted above it moves the coordinate | docs.yml | archanchors, selftest | gate | `--self-test` | `tools/arch-anchor-exceptions.txt` | `--min-files 40` |
| `tools/check-doc-cites.py` | python | a `file:line` in a normative document still points at a line, and a type it names is still built | docs.yml | cites, selftest | gate | `--self-test` | — | `--min-files 2 --min-cites 100 --min-types 50 --min-api 12 --min-counts 1 --min-test-cites 70 --min-guide-usings 30 --min-base-lists 2` |
| `tools/check-dup-jscpd.py` | python | the second duplication detector: jscpd over the whole maintained C# surface, against a committed snapshot of the clones the tree has today | docs.yml | dupjscpd, selftest | gate | `--self-test` | `tools/jscpd-baseline.json` | — |
| `tools/bin/harbor-gates-linux-x64 check-doc-facts` | compiled: `tools/gates/check-doc-facts` | a count the onboarding documents state still equals the count of tracked files | docs.yml | facts, selftest | gate | `--self-test` | — | `--min-facts 5` |
| `tools/check-gate-policy.py` | python | this table still describes the gates the repository runs | docs.yml | gate-policy, selftest | gate | `--self-test` | `docs/CI_GATES.md` | `--min-gates 42` |
| `tools/check-local-merge-loss.py` | python | a local squash-merge into dev dropped no content: what each merge removed is still missing | docs.yml | merge-loss, selftest | gate | `--self-test` | `tools/merge-loss-exceptions.txt` | `--min-merges 1` |
| `tools/check-md-links.py` | python | every internal link, anchor and `path/File.cs:123` reference resolves | docs.yml | links, selftest | gate | `--self-test` | — | `--min-files 250 --min-refs 600` |
| `tools/check-result-chains.py` | python | the chain candidates among the `if (x.IsFailure)` guard sites in `src/` and `apps/` — the census issue #1558 is measured against, and a ratchet on it | docs.yml | result-chains, selftest | gate | `--self-test` | `tools/result-chains-baseline.json` | — |
| `tools/check-string-switch.py` | python | does a `switch` compare a string literal, and is every ladder that is still there named with a reason | docs.yml | selftest, stringswitch | gate | `--self-test` | `tools/string-switch-exceptions.txt` | `--min-labels 280` |
| `tools/string-switch-rewrites.py` | python | shared data for the string-switch gate: the reviewed rewrites its `--apply` mode replays, so the change is reproducible and its effect measured rather than asserted | — | — | library | — | — | — |
| `tools/check-test-dup.py` | python | a test body is a copy of another test body | docs.yml | selftest, testdup | advisory | `--self-test` | `tools/test-dup-baseline.json` | `--min-files 800 --min-methods 9000` |
| `tools/check-test-json-reflection.py` | python | no test serializes through the reflection resolver | docs.yml | reflection-json, selftest | gate | `--self-test` | `tools/test-json-reflection-exceptions.txt` | — |
| `tools/check-test-packages.py` | python | which packages a test or sample project may reference, and whether a banned one is among them | ci.yml | build, test-packages | gate | `--self-test` | `tools/test-package-exceptions.txt` | `--min-projects 43 --min-declared 50 --min-packages 1400 --min-output-dirs 40` |
| `tools/check-test-projects.py` | python | every test project in the solution is discovered by a CI run | docs.yml | selftest, testprojects | gate | `--self-test` | `tools/test-project-baseline.txt` | `--min-projects 43 --min-tested 38 --min-lists 7 --min-claims 37 --min-contexts 41` |
| `tools/check-tool-schema-pin.py` | python | the parameter schema of every builtin tool still is the one that was pinned: the schema is the tool's contract with the model, and a schema that moved while the pin did not is a model that may send something the tool does not accept | docs.yml | selftest, tool-schema | gate | `--selftest` | — | `--min-tools 25` |
| `tools/check-tunit-analyzer-gate.py` | python | the control for the TUnit analyzer gate: the analyzer must report a planted offender, and a waiver must suppress exactly its own site | ci.yml | build | control | `--selftest` | — | — |
| `tools/changelog-assemble.py` | python | the changelog fragments are not already in the `[Unreleased]` section of `CHANGELOG.md` (the release would write them twice), and the assembly is idempotent. A RELEASE-time tool, not a gate on the pull-request path: a pull request owns its own `changelog/fragments/<pr-number>.md` and never edits `CHANGELOG.md` itself, so two pull requests never touch the same bytes | docs.yml | selftest | helper | `--self-test` | — | — |
| `tools/ci-shard-slices.py` | python | the CI project lists have ONE source (`tools/ci-project-lists.json`), and every place that names a test project to run it says what the source says: the `test` job's matrix, the build job's upload slices, the coverage list and the OS matrix. `--write` regenerates them; the gate is what runs in CI | ci.yml | build | gate | `--self-test` | — | `--min-shards 4 --min-projects 37` |
| `tools/codemod-test-events.py` | python | rewrites the hand-built test events and test messages in `tests/` into the shared `TestEvents` and `TestMessages` builders, and reports the forms it will not touch. A tool a human runs, and no workflow runs it | — | — | helper | — | — | — |
| `tools/codemod-tool-schemas.py` | python | rewrites the hand-written JSON parameter schemas of the builtin tools into the [ToolParam] declarations the generator renders, and writes the pin they are hashed into. A tool a human runs, and no workflow runs it | — | — | helper | — | — | — |
| `tools/demo_repro.py` | python | the demo playback is reproducible, and the GIFs are within their frame budget | demo.yml | demo-gifs, demo-reproducibility | gate | — | `assets/demo/baseline.json` | — |
| `tools/md-lint.py` | python | the tracked markdown obeys the invariants the repository claims | docs.yml | lint, selftest | gate | `--self-test` | — | `--min-files 250 --min-lines 60000` |
| `tools/gen-e2e-shards.py` | python | generates the process plan of the headless Avalonia E2E suite. A tool a human runs, and no workflow runs it | — | — | helper | — | — | — |
| `tools/md_gate.py` | python | shared plumbing for the two markdown gates: the tracked-file list, the floors, the fixture harness | — | — | library | — | — | — |
| `tools/run-e2e-avalonia-shards.py` | python | runs the headless Avalonia E2E plan, one process per shard, each with its own filter and screenshot directory | ci.yml | test | helper | — | — | — |
| `tools/normalize-mtimes.py` | python | the source mtimes tell the truth, so MSBuild can skip what did not change | ci.yml, docs.yml | build, selftest | helper | `--self-test` | — | — |
| `tools/pack-validate.py` | python | every packed `Harbor.*` NuGet package carries its metadata, its README and its lib | ci.yml, docs.yml | pack, selftest | gate | `--selftest` | — | — |
| `tools/tool_schema_model.py` | python | shared data for the tool-schema gates: the one reading of a tool's [ToolParam] declaration, so the pin is written from the same model the gate reads it with | — | — | library | — | — | — |
| `tools/check-arch-suite-once.py` | python | the architecture suite runs EXACTLY ONCE on the pull-request lane: the build job switches the build-time gate off for pull_request events, the `arch` shard runs the suite, and no other job does. Zero executions is a finding too, and so is a merge path that lost the gate | ci.yml | build | gate | `--self-test` | — | `--min-shards 4` |
| `tools/guard-triage.py` | python | what each architecture-guard firing MEANT: a defect, a style rule, or noise on code the repository allows | ci.yml, docs.yml | test, selftest | reporter | `--self-test` | `tools/guard-triage-classes.json` | `--min-guard-files 100` |
| `tools/check-shard-balance.py` | python | are the CI test shards balanced by what their projects cost: the heaviest shard's wall against the lightest one's, from the shard lists of the one source and the recorded cost of every project | docs.yml | selftest, shardbalance | gate | `--self-test` | `tools/shard-costs.json` | `--min-projects 37` |
| `tools/bin/harbor-gates-linux-x64 gen-seam-graph` | compiled: `tools/gates/gen-seam-graph` | the committed seam graph still matches the tree: who implements a seam, who consumes it, who is registered in the container, and what each route does with each extension axis | docs.yml | seamgraph, selftest | gate | `--self-test` | `docs/SEAM_GRAPH.md` | `--min-files 2523 --min-interfaces 191` |
| `tools/coverage_report.py` | python | the per-project and per-file coverage report of a run | docs.yml, nightly.yml | selftest, coverage | reporter | `--self-test` | `tools/coverage-registry.txt` | `--min-files 1200` |
| `tools/check-coverage-registry.py` | python | the coverage registry (which file is deliberately uncovered, and why) still matches the tree | docs.yml, nightly.yml | selftest, coverage | gate | `--self-test` | `tools/coverage-registry.txt` | `--min-covered 400 --min-files 1200` |
| `tools/gen-plugin-inventory.py` | python | the committed plugin inventory snapshot still matches the tree: the extension axes, the per-route verdicts and the plugins the repository ships, in the same schema the product writes at runtime for the live composition | docs.yml | plugininventory, selftest | gate | `--self-test` | `docs/PLUGIN_INVENTORY.json` | `--min-axes 7 --min-routes 7 --min-plugins 6` |
| `tools/codemod-tool-names.py` | python | rewrites the tool-name string literals in `src/` and `apps/` onto the `WellKnownTools` catalog (#1564), and reports the literals it will not touch. A tool a human runs, and no workflow runs it | — | — | helper | — | — | — |
| `tools/bin/harbor-gates-linux-x64 aggregate-build-vcpu` | compiled: `tools/gates/aggregate-build-vcpu` | the measurement table: the wall time, the CPU utilisation and the task aggregates of the runs, pooled per arm and divided by the baseline. The nightly lane's measurement job runs it over its two arms | nightly.yml | measurement | reporter | `--self-test` | — | — |
| `tools/bin/harbor-gates-linux-x64 check-abstractions-contract` | compiled: `tools/gates/check-abstractions-contract` | every dependency the five guarded `src/` projects declare is load-bearing | docs.yml | contract, selftest | gate | `--self-test` | — | `--min-packages 19 --min-namespaces 7 --min-files 141` |
| `tools/bin/harbor-gates-linux-x64 check-no-test-sdk` | compiled: `tools/gates/check-no-test-sdk` | no project file declares `Microsoft.NET.Test.Sdk`, the VSTest host that stops TUnit's discovery | ci.yml | test-sdk-absent | gate | `--self-test` | — | `--min-files 150` |
| `tools/bin/harbor-gates-linux-x64 check-public-api-resign` | compiled: `tools/gates/check-public-api-resign` | a change to the frozen surface is re-signed in the same commit, no row of a frozen baseline is written twice, and a baseline edit that only removes a row is a correction rather than a re-signing | ci.yml, docs.yml | public-api, selftest | gate | `--self-test` | `src/Harbor.Abstractions.Contracts/PublicAPI.Shipped.txt` | `--min-rows src/Harbor.Abstractions.Contracts=1896 --min-rows src/Harbor.Abstractions=1269` |
| `tools/bin/harbor-gates-linux-x64 check-reference-trimmer` | compiled: `tools/gates/check-reference-trimmer` | does any project in the tree reference a package or a sibling project the compiler never uses | ci.yml | reference-trimmer | gate | `--self-test` | `tools/reference-trimmer-baseline.txt` | `--min-projects 100` |
| `tools/bin/harbor-gates-linux-x64 check-xml-doc-coverage` | compiled: `tools/gates/check-xml-doc-coverage` | every public member of the frozen contract assembly carries an XML doc | docs.yml | selftest, xmldoc | gate | `--self-test` | — | `--min-members 493 --min-documented 478 --min-coverage 0.96` |
| `tools/bin/harbor-gates-linux-x64 measure-build-cpu` | compiled: `tools/gates/measure-build-cpu` | what a build command costs: the wall time, the CPU utilisation sampled from `/proc/stat`, the CPU accounted per process, and MSBuild's own task aggregates | meas-build-vcpu.yml, meas-analyzer-cost.yml, nightly.yml | measure, measurement | reporter | `--self-test` | — | — |
| `tools/bin/harbor-gates-linux-x64 measure-project-graph` | compiled: `tools/gates/measure-project-graph` | the shape of the project dependency graph: the depth, the level widths and the critical path, parsed from the solution and the `ProjectReference` edges. A measurement a human runs; only its self-test is called by CI | docs.yml | selftest | helper | `--self-test` | — | — |
| `tools/bin/harbor-gates-linux-x64 msbuild-evaluation-cost` | compiled: `tools/gates/msbuild-evaluation-cost` | what a Release compile spends on MSBuild project evaluation, read from the performance summary | nightly.yml | measurement | reporter | `--self-test` | — | — |
| `tools/bin/harbor-gates-linux-x64 test-census` | compiled: `tools/gates/test-census` | the per-test and per-project wall census of a CI run | ci.yml, docs.yml | selftest, test | reporter | `--self-test` | — | — |
| `tools/bin/harbor-gates-linux-x64 test-measurements` | compiled: `tools/gates/test-measurements` | the per-test measurements a CI run printed | ci.yml, docs.yml, test-os.yml | selftest, test, test-os | reporter | `--self-test` | — | — |
| `tools/bin/harbor-gates-linux-x64 test-skips` | compiled: `tools/gates/test-skips` | the tests a run skipped, and the reason each declared | ci.yml, docs.yml, test-os.yml | selftest, test, test-os | reporter | `--self-test` | — | — |
| `tools/bin/harbor-gates-linux-x64 check-analyzer-scope` | compiled: `tools/gates/check-analyzer-scope` | where each analyzer package is allowed to run: the scope table in `tools/analyzer-scope.json` against the tree, so a family cannot spread back to every project | ci.yml | analyzer-scope | gate | `--self-test` | `tools/analyzer-scope.json` | `--min-files 150` |
| `tools/bin/harbor-gates-linux-x64 aggregate-analyzer-cost` | compiled: `tools/gates/aggregate-analyzer-cost` | the analyzer cost table: what each analyzer pack costs the compiler, summed over the builds | meas-analyzer-cost.yml | aggregate | reporter | `--self-test` | — | — |
Two things the table says that are easy to miss:

- The compiled `msbuild-evaluation-cost` gate measures time, and it is NOT a
  gate. It
  is a reporter in a workflow of its own, and the policy's "a gate must not
  measure time" rule applies to gates alone: a measurement that fails a build
  on a slow runner is a gate that gets switched off, and a measurement that
  only reports belongs where this one is.
- `tools/demo_repro.py` has no self-test. It is a gate, and it is the one
  gap left in the "every gate has a self-test" rule. It compares two
  recordings of the same tape (the demo played twice), so a planted
  control for it is a fixture of that shape rather than a flag.
- `tools/ci-shard-slices.py` USED to have no self-test, for the same
  reason: it compared two things CI itself produced (the shard list
  against the slice list, two recordings of the same tape). Since the
  lists came from one source (issue #1800), the fixture is a desync
  planted in the tree rather than a flag, and the gate has one.

## The checker

[`tools/check-gate-policy.py`](../tools/check-gate-policy.py) is the rule
above, in code. It reads three sources - the table, the scripts in `tools/`,
and the workflow files - and fails when they disagree:

| Rule | What it catches |
|---|---|
| `POLICY-SCRIPT-UNDOCUMENTED` | a script CI runs that the table does not name |
| `POLICY-INVOCATION-DEAD` | a workflow running a script that is not in the tree |
| `POLICY-WORKFLOW-UNDECLARED` | a script run by a workflow the table does not name, or a workflow the table names that does not run it |
| `POLICY-JOB-UNKNOWN` | a job the table names that does not exist, or that does not run the script |
| `POLICY-MODE-GATE-NO-RUN` | a gate whose only CI use is its own self-test |
| `POLICY-MODE-GATE-WARN-ONLY` | a gate CI runs with `--warn-only` |
| `POLICY-MODE-ADVISORY-NOT-WARN-ONLY` | an advisory gate CI runs without `--warn-only` |
| `POLICY-SELFTEST-UNSUPPORTED` | a self-test flag the script does not accept, or a script that has one the table denies |
| `POLICY-SELFTEST-NOT-RUN` | a script with a self-test that no workflow ever runs |
| `POLICY-FLOORS-DIVERGE` | floors the table claims that CI does not pass, and floors CI passes that the script does not accept |
| `POLICY-BASELINE-MISSING` | a ratchet the table names that is not in the tree, or that the script never reads |
| `POLICY-HEADER-MISSING` | a gate whose header does not say why it is python |
| `POLICY-DOTNET-CALL` | a gate that shells out to dotnet |
| `POLICY-TIME-CALL` | a gate that measures time |
| `POLICY-SCRIPT-MISSING` | a row naming a script that is not in the tree |
| `POLICY-DUPLICATE-SCRIPT` | two rows for one script |
| `POLICY-TABLE-EMPTY` | a table that lost rows |

The rules it does not enforce are the ones no file can answer: what a gate
checks, that its message names the violator, that it has no allow-list, and
that it does not duplicate another gate. Those are reviewed by a human, and
the document says so rather than pretending otherwise.

Run it:

```bash
./tools/check-gate-policy.py --self-test
./tools/check-gate-policy.py --min-gates 38
```

Its own `--self-test` plants one defect per rule in a synthetic tree and
asserts that the rule fires, and that the same tree without the defect is
clean. A rule that cannot fire is indistinguishable from a rule that found
nothing, which is the failure every gate in this repository is tested
against.

## Adding a gate

1. Decide the question first, and put it in the header of the script. If the
   question needs a type or a symbol, it is an analyzer, not a gate here.
2. Write the failure path first: the planted control, the exit code, and the
   message. A gate whose message does not name the violator will be rewritten
   later, and the rewrite is the expensive part.
3. Give it a floor, and set the floor at the measured value. A floor below the
   measurement cannot tell "the tree got smaller" from "the matcher stopped
   matching".
4. Give it a ratchet, if the tree already violates the rule. The ratchet is a
   committed file that can only shrink.
5. Add it to the table above, and add its self-test to the `selftest` job of
   `docs.yml` (or to the job that runs it). The checker will say so if you
   forget, which is the point of the checker.
6. List it in [`tools/README.md`](../tools/README.md), whose first rule is
   that every script is listed.
7. Add the script to the `paths:` list of `docs.yml`, so that a pull request
   that edits the gate runs it. A gate made green by never running is the
   failure this repository has already paid for three times.

### Adding a COMPILED gate

A gate that costs more than a couple of seconds on every pull request is a
gate that should be compiled. The steps are the same as above, with the
implementation living in `tools/gates/<gate>/` instead of in
`tools/<gate>.py`:

1. The gate's name is the name of its directory and the name of its
   subcommand: `tools/gates/gen-seam-graph/` is `harbor-gates gen-seam-graph`.
2. The sources use the standard library only, and there is no `go.sum`: a
   gate that fetched something at run time could not run on a runner with no
   network.
3. The flags and the printed text are the ones the python original had.
   Compare the output of the two on the same tree BEFORE the original is
   deleted: the same exit code, the same text, the same verdicts.
4. `tools/build-gates.sh` builds the binary for both architectures and
   writes `tools/bin/SHA256SUMS`. Commit the binary with the sources: the
   binary IS the gate, and the sources without it are not a gate.
5. Delete the python original in the same pull request. A python original
   and a compiled twin of the same gate are two implementations of one
   gate, and they drift.
6. The workflow runs `tools/bin/harbor-gates-linux-x64 <gate> ...`, and the
   table row names that command and the source directory.
