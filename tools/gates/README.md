# The compiled gates

The gates of this repository used to be python scripts. They are becoming
subcommands of ONE binary, `harbor-gates <gate> [flags]`, whose bytes are
committed to the repository.

## Why a binary in git

The rule this repository already states for its gates: **a gate has to fire
where the SDK and the build are NOT**, because a documentation-only pull
request and the pull request that breaks the build are exactly the ones
that need it. `ci.yml` ignores `**.md` and `docs/**` on purpose, so a
documentation pull request never starts the .NET build, and it gets a second
workflow, `docs.yml`, which is deliberately SDK-free. A compile step on the
runner breaks that rule: the check of the documentation of the pull request
that fixes the build would need the build to work first.

A committed binary satisfies the rule. The repository already does the same
thing for the demo GIFs: `.github/workflows/demo.yml` regenerates
`assets/demo/*` and commits it.

The price of a binary in git is that nobody can read it in review. The
answer is a machine check, not a promise: the `gates-binary` workflow
rebuilds the binary from the sources in the same tree and compares the bytes
with the committed ones, and a pull request that hand-edits the binary, or
that ships a stale one, goes red.

## The contract

```
harbor-gates <gate> [flags]
```

| exit | meaning |
|---|---|
| 0 | green: the gate ran and found nothing |
| 1 | red: the gate ran and found something |
| 2 | the gate could not run: a bad invocation, a missing input, an unreadable file |

"Could not run" is not "found nothing". A gate that cannot start must not
look like a gate that found nothing.

Each gate keeps the flags its python original had, with the same names and
the same floors, and prints the same human-readable text, because the
workflows read those lines and the self-tests compare them.

## The tree gates, in one command

```bash
harbor-gates all            # every gate that needs nothing but the tree
harbor-gates all --list     # the list of them, and what each one reads
harbor-gates all --pending  # the ones that are still python, as commands
harbor-gates all --self-test
```

The gates of this repository come in three kinds, and the kind decides where
a gate can run: a gate that reads what the BUILD produced (a TRX, a publish
log, a `.nupkg`), a gate that runs `dotnet` itself, and a gate that reads the
TREE. The third kind is what `all` runs, and it runs them in parallel and
prints one table:

```
gate                          code  time       first line of the output
----------------------------  ----  ---------  --------------------------------
check-abstractions-contract   0     1.890 s    (no output)
check-analyzer-scope          0     634 ms     OK: 159 project file(s) scanned, ...
```

The exit code is the contract above: 0 green, 1 red, 2 could not run. A
gate that could not run is not a gate that found nothing, and a run where
one gate could not start is broken even if the others are green.

The list of the tree gates is in [`all/gates.go`](./all/gates.go), and it is
checked against the workflows: a gate that a workflow runs with a TRX or
with `dotnet` on the command line is not a tree gate and cannot be in the
list, and a gate no workflow runs cannot be in it either. The gates that are
still python are listed as "not run" with the reason, and
`tools/check-local.sh` runs them, so the local command and the CI command
cover the same gates.

## The layout

```
tools/gates/
  go.mod                  the module: standard library only, no external dependency
  main.go                 the dispatcher: one subcommand per gate
  internal/gate/          what the gates share: exit codes, the repository root, the file list
  <gate-name>/            one directory per gate; the directory name is the subcommand
tools/bin/
  harbor-gates-linux-x64
  harbor-gates-linux-arm64
  SHA256SUMS              the hash of each committed binary
```

The gate's name is the name of its directory, and the name of the subcommand
that runs it: one directory, one gate, one workflow line.

## The build

```bash
tools/build-gates.sh            # build both, write the manifest
tools/build-gates.sh --check    # build both, compare with the committed bytes, fail on a difference
tools/build-gates.sh --verify   # build both TWICE and compare the two builds with each other
```

The build is byte-reproducible:

```
CGO_ENABLED=0 go build -trimpath -ldflags="-buildid= -s -w"
```

`CGO_ENABLED=0` makes the binary static, with no libc and no dynamic
loader, so it runs on any linux of the same architecture. `-trimpath` keeps
the absolute paths of the checkout out of the binary, so the same source
builds the same bytes in any directory. `-buildid=` drops the build id,
which is otherwise a hash of the build environment and changes on every
machine. `-s -w` drop the symbol table and the DWARF.

The toolchain is pinned by `tools/gates/go.mod` (the `go` directive) and the
workflow reads it from there with `actions/setup-go`, so a toolchain bump is
an explicit, reviewable change.

## The rules

1. **One gate, one pull request.** The binary change, the deleted python
   original, the row in `docs/CI_GATES.md`, and the self-test. No window
   where neither the python nor the binary works.
2. **No python original and no compiled twin of the same gate.** The twin is
   how a drifted pair is caught, and `tools/check-gate-policy.py` fails on
   it.
3. **The same verdict on the same tree.** The same exit code, the same
   output, the same flags. The port is verified by comparing the output
   with the python original's before the original is deleted.
4. **Standard library only.** No external module, and no `go.sum`: a gate
   that fetched something at run time could not run on a runner with no
   network.
5. **The self-test still fails on a planted divergence.** A gate that cannot
   say no is a gate that enforces nothing.
