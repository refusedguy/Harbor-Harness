# The gate benchmark: how the repository's own gates are timed

`measure.sh` times the gate suite the way CI runs it, and prints one row per
gate. It answers the question the port to a compiled language has to answer
with numbers: **what does the whole set cost?**

The single-gate number and the whole-set number are not the same number. A
gate that costs 22 seconds alone may be 5 percent of a suite that costs 197
seconds, and a change that makes that one gate 8 times faster then buys 5
percent of the suite. The suite is what a pull request pays.

## What is measured

| | |
|---|---|
| the tree | a clean checkout of the commit under test, with the git history (the gates `check-doc-cites.py` and `check-local-merge-loss.py` read the history, and a tree without it is not the tree CI runs) |
| the run | every gate, with the arguments its workflow passes, from `tools.tsv` |
| the number | the MINIMUM wall time over `RUNS` runs, and the peak RSS of the child |
| the parent | `maxrss.c`, a 2 KB C program |

The minimum is the estimate that the least foreign work distorts. This
machine is shared, and its load is 18-26, so a single run says more about
the neighbours than about the gate.

The peak RSS is `/proc/<pid>/status VmHWM`, polled while the child runs.
`getrusage(RUSAGE_CHILDREN).ru_maxrss` is printed next to it as a
diagnostic and is NOT the number the table uses: on this host it carries a
value the child never reached (a run that slept for 300 ms with a 10 MB
footprint reported 109 MB), because it inherits the accounting of the
parent's own high-water mark.

## The set is the set CI runs

`bench/gates/tools.tsv` is the measured set: one row per gate, TAB-separated,
the gate's name and the command the harness runs it with. The arguments are
the arguments the workflow passes. The value of an artifact path is the
harness's business; the flags are the gate's business.

`bench/gates/excluded.tsv` holds the gates CI runs that the measured set does
NOT hold, each with the reason it is out.

The two files and the workflow files are checked against each other by
`harbor-gates check-gate-bench-set`, which is a CI gate of this repository
(the `benchset` job of `docs.yml`). It fails when:

| rule | what it catches |
|---|---|
| `BENCH-SET-GATE-MISSING` | a gate a workflow runs is in neither file: the set is smaller than what a pull request pays |
| `BENCH-SET-GATE-STALE` | a row names a gate no workflow runs |
| `BENCH-SET-NO-REASON` | an excluded gate carries no reason |
| `BENCH-SET-README-SILENT` | an excluded gate is not named in this file: a reason that lives only in a data file is a reason nobody reads |
| `BENCH-SET-ARGS-DIVERGE` | the measured row does not pass the arguments CI passes, so the benchmark times a different gate |
| `BENCH-SET-COMMAND-DEAD` | the measured row runs a file that is not in the tree |
| `BENCH-SET-ROW-SHAPE` | a row is not the name and the command |

So the set cannot drift away from CI: a gate added to a workflow without a
row in `tools.tsv` is red, and a row whose arguments fell behind the
workflow's is red too. The instrument that times the set is checked by the
gate that checks the set.

## The gates that are NOT in the set

Three reasons a gate CI runs is out of the measured set, and only three.

| gate | why it is out |
|---|---|
| `changelog-assemble` | CI runs only its own self-test: the assembler is a release-time tool, and a pull request owns its own fragment |
| `check-dup-jscpd.py` | the gate downloads the jscpd binary from the network on first use, and there is no network and no binary on the measuring machine |
| `check-tunit-analyzer-gate.py` | the control runs the compiler: it builds a throwaway project and reads its output, and the measuring machine has no .NET SDK |
| `demo_repro.py` | the demo is recorded by running Harbor and the gate compares two recordings of it, so it needs the built product and the .NET SDK |
| `measure-aot-workload.sh` | the workload publishes the CLI with NativeAOT and runs it, so it needs the .NET SDK |
| `measure-build-cpu` | the instrument runs `dotnet build` and meters it, so it needs the .NET SDK; the numbers it produces live in [docs/BENCHMARKS.md](../../docs/BENCHMARKS.md) §6 |
| `measure-jit-workload.sh` | the workload builds the CLI and runs it, so it needs the .NET SDK |
| `measure-project-graph` | CI runs only its own self-test: the project graph is read by a human, not by a build |
| `msbuild-evaluation-cost` | the measurement reads the performance summary of a `dotnet build`, so it needs the .NET SDK; the numbers it produces live in [docs/BENCHMARKS.md](../../docs/BENCHMARKS.md) §6 |
| `public-api-nonvacuity.sh` | the proof builds a throwaway project against the public-API analyzer and requires it to fail, so it needs the .NET SDK |
| `run-e2e-avalonia-shards` | the runner starts one `dotnet exec` per shard, so it needs the built product and the .NET SDK |
| `gen-e2e-shards` | CI runs only its own self-test: the shard plan is a generator a human runs before a release, and no build reads it |

The gates that need the SDK are not "cheap" and not "gone": they are the
compile-time gates, and the numbers they produce are in
[docs/BENCHMARKS.md](../../docs/BENCHMARKS.md) §6. The measured set is the
set a pull request pays ON A MACHINE THAT HAS NO SDK, which is the machine
this repository's own gates have to run on.

## The gates that read an artifact

A gate that judges an artifact the build produced is measured against an
EMPTY directory of the right shape. Its number is then the cost of walking
the tree and of finding nothing, not the cost of parsing a real artifact.
That is stated here, and it is a lower bound: the parse is not in the
number.

| gate | the artifact the gate reads | what the row measures |
|---|---|---|
| `aot-publish-gate` | `publish/aot-publish.log` | the walk to the log; the log is empty, so the gate reports that it could not judge the publish (exit 2) |
| `aggregate-analyzer-cost` | `meas/` | the walk of an empty measurement directory |
| `aggregate-build-vcpu` | `meas/` | the walk of an empty measurement directory |
| `check-coverage-registry.py` | `Coverage/` | the walk of an empty coverage directory |
| `coverage_report.py` | `Coverage/` | the walk of an empty coverage directory |
| `check-reference-trimmer` | `rt-warnings.log`, `rt-coverage.txt` | the walk of two empty files |
| `check-test-packages.py` | `obj/project.assets.json` of every test project | the walk of the project list; the resolved package graph is not there, because a restore needs the SDK |
| `gen-seam-graph` | the tree itself | the whole scan; this gate reads the tree, not an artifact |
| `guard-triage.py` | `TestResults/*.trx` | the walk of an empty results directory |
| `pack-validate.py` | `out/packages/*.nupkg` | the walk of an empty package directory |
| `test-census`, `test-measurements`, `test-skips` | `TestResults/*.trx` | the walk of an empty results directory |

The gate `check-local-merge-loss.py` needs the git history and the pull
request heads; the measuring machine has the history of the repository it
shares the object store with, and the heads it has fetched so far. The
`--allow-missing-heads` flag is the one CI passes, so a head the machine does
not have is skipped rather than an error.

The gate `check-public-api-resign` needs a base commit; the harness passes
`HEAD~1`, so the range it walks is one commit rather than a pull request's
whole range.

## usage

```bash
# the gate suite, as CI runs it
TREE=/path/to/clean/tree RUNS=5 ./measure.sh > gates.tsv
```

`measure.sh` reads `tools.tsv`, prepares the empty artifact shapes the
gates read, and runs every row `RUNS` times. `TREE` is the tree under test
and `RUNS` is the number of runs (5 is the number the numbers in
[docs/BENCHMARKS.md](../../docs/BENCHMARKS.md) §10.2 were taken with).

There is one command per gate and no variant column: fourteen gates moved
from python into the committed binary, and their python originals are
deleted, so a "python" run of the set is not a run of anything that exists
any more. The python numbers of the old set are in
[docs/BENCHMARKS.md](../../docs/BENCHMARKS.md) §10.2, measured on the tree
that still had them.

## Why the measuring parent is a C program

A python parent forks with the interpreter's whole address space already
resident, and the child's `ru_maxrss` inherits that accounting: every number
it reports is floored at the parent's RSS, which for the interpreter is
about 12 MB. A 2 KB C parent has an address space of about 1.3 MB, so the
number it prints is the child's own.

## Why the tree is a worktree and not a clone

The gate reads the git history, and `git blame` over a history that was
fetched with `--depth 1` attributes every line to the shallow boundary
commit. A clone would have to be a full clone to be honest about that, and a
full clone of this repository is expensive. A worktree shares the object
store of the repository it was cut from, and it has the same history.
