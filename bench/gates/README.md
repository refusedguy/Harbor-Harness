# The gate benchmark: how the repository's own gates are timed

`measure.sh` times the gate suite the way CI runs it, and prints one row per
gate. It answers the question the port to a compiled language has to answer
with numbers: **what does the whole set cost, and what does the language buy
on the whole set?**

The single-gate number and the whole-set number are not the same number. A
gate that costs 22 seconds alone may be 5 percent of a suite that costs 197
seconds, and a change that makes that one gate 8 times faster then buys 5
percent of the suite. The suite is what a pull request pays.

## What is measured

| | |
|---|---|
| the tree | a clean checkout of the commit under test, with the git history (the gate `TEST-CITE-DRIFT` reads the history, and a tree without it is not the tree CI runs) |
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

## usage

```bash
# the python gate, as CI runs it
TREE=/path/to/clean/tree RUNS=5 VARIANT=python ./measure.sh > python.tsv

# the compiled port
TREE=/path/to/clean/tree RUNS=5 VARIANT=go ./measure.sh > go.tsv
```

`VARIANT` picks the column of `tools.tsv`. A gate with no port yet has an
empty cell, and the row says so instead of borrowing the python number.

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
