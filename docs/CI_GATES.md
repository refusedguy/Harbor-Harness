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

Measured on `dev` at `15b5e44c`, the tree this policy landed on:

| | |
|---|---|
| python tools in `tools/` | 27 files, 22 427 lines |
| of them, the `check-*.py` family | 14 files, 16 928 lines |
| workflows that run a python tool | 5: `ci.yml`, `docs.yml`, `demo.yml`, `evaluation-cost.yml`, `test-os.yml` |
| gates that block a build | 18 |
| gates that report and let the build pass | 1 (`check-test-dup.py`, `--warn-only` while the removal programme runs) |
| reporters, helpers, libraries | 4 reporters, 3 helpers, 1 library |
| tools with a self-test | 22 |
| tools with a ratchet | 10 |
| tools with a floor | 13 |
| tools whose self-test no workflow ran before this change | 3 (`pack-validate.py`, `test-skips.py`, `normalize-mtimes.py`) |
| gates in a workflow that runs a script that is not in the tree | 1 (`ci.yml`'s `public-api-abstractions` job, left behind by the #1845 squash) |

The last two rows are the two gaps this policy closed on the way in, and both
were found by the checker rather than by reading: the checker was run against
the tree before the table was written, and it named them.

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

## Why the gates are python

The reason is not speed. All the python gates together take seconds, and
moving them to C# would add a restore, a build and a publish before each one.

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
restore. A standard-library python script is the only thing in the tree that
does that, and `tools/` is where it lives.

Three reasons, and only three, put a check in python:

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
tree. So the re-signing rule is
[`tools/check-public-api-resign.py`](../tools/check-public-api-resign.py), and
it is in the table below.

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
2. **A ratchet, and a floor that only goes down.** The floor is the
   non-vacuity proof in numeric form: a scan that quietly narrowed must go
   red instead of reporting a clean tree. A floor is lowered in the same diff
   as the change that legitimately lowered the number, where a reviewer sees
   it. A floor that can be raised is a target, not a ratchet.
3. **A deterministic exit.** The same tree gives the same exit code. No
   clock, no network, no randomness, no dependence on the machine.
4. **A message that names the violator.** "3 problems" costs the reader the
   whole run; the path, the line and the member cost nothing.
5. **No allow-list of files to skip, and no `continue-on-error`.** A skipped
   set is invisible at review time, and a gate that cannot fail is
   decoration.
6. **A header that says why it is python.** One line, near the top, in the
   form `Stdlib only - no pip, no network, no dotnet.`

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

Every python tool in `tools/` (33 files). 29 of them are run by a workflow; the
other four are `gen-e2e-shards.py`, a generator a human runs, `md_gate.py`,
which is imported by two of the others rather than run, and the two
measurement helpers `aggregate-build-vcpu.py` and `measure-project-graph.py`,
which a human runs after a measurement run. All 33 are listed, because a
tool that is not listed is a tool nobody reviews., with the question
it answers, where it runs, whether it blocks a build, whether it has a
self-test, the ratchet it holds, and the floors CI passes it. The table is
checked by `tools/check-gate-policy.py`, which fails when it stops describing
the tree.

The modes are:

- **gate** - it fails a build.
- **advisory** - it reports and lets the build pass (`--warn-only`).
- **reporter** - it prints and never fails.
- **helper** - a tool the pipeline uses, not a check of its own.
- **library** - imported by other tools, run by no workflow.

| Script | What it checks | Runs in | Jobs | Mode | Self-test | Baseline | Floors |
|---|---|---|---|---|---|---|---|
| `tools/aggregate-build-vcpu.py` | the measurement table: the wall time, the CPU utilisation and the task aggregates of the runs, pooled per arm and divided by the baseline. A tool a human runs, and no workflow runs it | — | — | helper | — | — | — |
| `tools/aot-publish-gate.py` | the AOT publish's outcome and diagnostics against the committed record | ci.yml | aot-publish | gate | `--selftest` | `.github/aot-warning-baseline.txt` | — |
| `tools/check-abstractions-contract.py` | every dependency the four guarded `src/` projects declare is load-bearing | docs.yml | contract, selftest | gate | `--self-test` | — | `--min-packages 13 --min-namespaces 7 --min-files 130` |
| `tools/check-arch-anchors.py` | may an architecture rule anchor on a LINE NUMBER: a `file:line` is a coordinate, and every line inserted above it moves the coordinate | docs.yml | archanchors, selftest | gate | `--self-test` | `tools/arch-anchor-exceptions.txt` | `--min-files 40` |
| `tools/check-doc-cites.py` | a `file:line` in a normative document still points at a line, and a type it names is still built | docs.yml | cites, selftest | gate | `--self-test` | — | `--min-files 2 --min-cites 100 --min-types 50 --min-api 12 --min-counts 1 --min-test-cites 70 --min-guide-usings 30 --min-base-lists 2` |
| `tools/check-dup-jscpd.py` | the second duplication detector: jscpd over the whole maintained C# surface, against a committed snapshot of the clones the tree has today | docs.yml | dupjscpd, selftest | gate | `--self-test` | `tools/jscpd-baseline.json` | — |
| `tools/check-doc-facts.py` | a count the onboarding documents state still equals the count of tracked files | docs.yml | facts, selftest | gate | `--self-test` | — | `--min-facts 5` |
| `tools/check-gate-policy.py` | this table still describes the gates the repository runs | docs.yml | gate-policy, selftest | gate | `--self-test` | `docs/CI_GATES.md` | `--min-gates 27` |
| `tools/check-local-merge-loss.py` | a local squash-merge into dev dropped no content: what each merge removed is still missing | docs.yml | merge-loss, selftest | gate | `--self-test` | `tools/merge-loss-exceptions.txt` | `--min-merges 1` |
| `tools/check-no-test-sdk.py` | no project file declares `Microsoft.NET.Test.Sdk`, the VSTest host that stops TUnit's discovery | ci.yml | test-sdk-absent | gate | `--self-test` | — | `--min-files 150` |
| `tools/check-md-links.py` | every internal link, anchor and `path/File.cs:123` reference resolves | docs.yml | links, selftest | gate | `--self-test` | — | `--min-files 250 --min-refs 600` |
| `tools/check-public-api-resign.py` | a change to the frozen surface is re-signed in the same commit | ci.yml, docs.yml | public-api, selftest | gate | `--self-test` | `src/Harbor.Abstractions.Contracts/PublicAPI.Shipped.txt` | `--min-rows src/Harbor.Abstractions.Contracts=1896 --min-rows src/Harbor.Abstractions=1269` |
| `tools/check-result-chains.py` | the chain candidates among the `if (x.IsFailure)` guard sites in `src/` and `apps/` — the census issue #1558 is measured against, and a ratchet on it | docs.yml | result-chains, selftest | gate | `--self-test` | `tools/result-chains-baseline.json` | — |
| `tools/check-reference-trimmer.py` | does any project in the tree reference a package or a sibling project the compiler never uses | ci.yml | reference-trimmer | gate | `--self-test` | `tools/reference-trimmer-baseline.txt` | `--min-projects 100` |
| `tools/check-test-dup.py` | a test body is a copy of another test body | docs.yml | selftest, testdup | advisory | `--self-test` | `tools/test-dup-baseline.json` | `--min-files 800 --min-methods 9000` |
| `tools/check-test-json-reflection.py` | no test serializes through the reflection resolver | docs.yml | reflection-json, selftest | gate | `--self-test` | `tools/test-json-reflection-exceptions.txt` | — |
| `tools/check-test-packages.py` | which packages a test or sample project may reference, and whether a banned one is among them | ci.yml | build, test-packages | gate | `--self-test` | `tools/test-package-exceptions.txt` | `--min-projects 43 --min-declared 50 --min-packages 1400 --min-output-dirs 40` |
| `tools/check-test-projects.py` | every test project in the solution is discovered by a CI run | docs.yml | selftest, testprojects | gate | `--self-test` | `tools/test-project-baseline.txt` | `--min-projects 43 --min-tested 38 --min-lists 7 --min-claims 37 --min-contexts 41` |
| `tools/check-tunit-analyzer-gate.py` | TUnit's own analyzers are armed: a planted offender fails the build and a planted waiver does not | ci.yml | build | gate | `--selftest` | — | — |
| `tools/check-xml-doc-coverage.py` | every public member of the frozen contract assembly carries an XML doc | docs.yml | selftest, xmldoc | gate | `--self-test` | — | `--min-members 493 --min-documented 478 --min-coverage 0.96` |
| `tools/ci-shard-slices.py` | the build-output slices match the test shards | ci.yml | build | gate | — | — | — |
| `tools/demo_repro.py` | the demo playback is reproducible, and the GIFs are within their frame budget | demo.yml | demo-gifs, demo-reproducibility | gate | — | `assets/demo/baseline.json` | — |
| `tools/md-lint.py` | the tracked markdown obeys the invariants the repository claims | docs.yml | lint, selftest | gate | `--self-test` | — | `--min-files 250 --min-lines 60000` |
| `tools/gen-e2e-shards.py` | generates the process plan of the headless Avalonia E2E suite. A tool a human runs, and no workflow runs it | — | — | helper | — | — | — |
| `tools/md_gate.py` | shared plumbing for the two markdown gates: the tracked-file list, the floors, the fixture harness | — | — | library | — | — | — |
| `tools/measure-build-cpu.py` | what a build command costs: the wall time, the CPU utilisation sampled from `/proc/stat`, the CPU accounted per process, and MSBuild's own task aggregates | meas-build-vcpu.yml | measure | reporter | — | — | — |
| `tools/measure-project-graph.py` | the shape of the project dependency graph: the depth, the level widths and the critical path, parsed from the solution and the `ProjectReference` edges. A tool a human runs, and no workflow runs it | — | — | helper | — | — | — |
| `tools/msbuild-evaluation-cost.py` | what a Release compile spends on MSBuild project evaluation, read from the performance summary | ci.yml, evaluation-cost.yml | build, measurement | reporter | `--self-test` | — | — |
| `tools/run-e2e-avalonia-shards.py` | runs the headless Avalonia E2E plan, one process per shard, each with its own filter and screenshot directory | ci.yml | test | helper | — | — | — |
| `tools/normalize-mtimes.py` | the source mtimes tell the truth, so MSBuild can skip what did not change | ci.yml, docs.yml | build, selftest | helper | `--self-test` | — | — |
| `tools/pack-validate.py` | every packed `Harbor.*` NuGet package carries its metadata, its README and its lib | ci.yml, docs.yml | build, selftest | gate | `--selftest` | — | — |
| `tools/test-census.py` | the per-test and per-project wall census of a CI run | ci.yml, docs.yml | selftest, test | reporter | `--self-test` | — | — |
| `tools/test-measurements.py` | the per-test measurements a CI run printed | ci.yml, docs.yml, test-os.yml | selftest, test, test-os | reporter | `--self-test` | — | — |
| `tools/test-skips.py` | the tests a run skipped, and the reason each declared | ci.yml, docs.yml, test-os.yml | selftest, test, test-os | reporter | `--self-test` | — | — |
Two things the table says that are easy to miss:

- `tools/msbuild-evaluation-cost.py` measures time, and it is NOT a gate. It
  is a reporter in a workflow of its own, and the policy's "a gate must not
  measure time" rule applies to gates alone: a measurement that fails a build
  on a slow runner is a gate that gets switched off, and a measurement that
  only reports belongs where this one is.
- `tools/ci-shard-slices.py` and `tools/demo_repro.py` have no self-test.
  They are gates, and they are the two gaps in the "every gate has a
  self-test" rule. Both compare two things that CI itself produced (the
  shard list against the slice list, two recordings of the same tape), so a
  planted control for them is a fixture of that shape rather than a flag.

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
./tools/check-gate-policy.py --min-gates 27
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
