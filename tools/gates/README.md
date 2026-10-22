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

## The gates

One directory per gate, and the directory name is the subcommand that runs
it. `harbor-gates --list` prints the same table.

| Gate | What it does |
|---|---|
| `aggregate-analyzer-cost` | the analyzer cost table: what each analyzer pack costs the compiler |
| `aggregate-build-vcpu` | the measurement table: the wall time, the CPU utilisation and the ratios |
| `aot-publish-gate` | the AOT publish's outcome and diagnostics match the committed record |
| `changelog-assemble` | the changelog fragments are not already in CHANGELOG.md, and the assembly is idempotent |
| `check-abstractions-contract` | every dependency the five guarded src/ projects declare is load-bearing |
| `check-analyzer-scope` | where each analyzer package is allowed to run |
| `check-coverage-registry` | the registry of consciously uncovered files still matches the measurement |
| `check-doc-facts` | a count a document states still equals the count of tracked files |
| `check-event-topology` | the event-topology document's producer rows name the tree's real PublishAsync call sites |
| `check-local-merge-loss` | a local squash-merge into dev dropped no content |
| `check-no-test-sdk` | no project file declares the VSTest host that stops TUnit's discovery |
| `check-public-api-resign` | a change to the frozen surface is re-signed in the same commit |
| `check-reference-trimmer` | does any project in the tree reference a package the compiler never uses |
| `check-shard-balance` | are the CI test shards balanced by what their projects cost |
| `check-xml-doc-coverage` | every public member of the frozen contract assembly carries an XML doc |
| `codemod-test-events` | the hand-built test events and messages move onto the shared builders |
| `codemod-tool-names` | the tool-name literals move onto the WellKnownTools catalog |
| `coverage-report` | the per-project and per-file coverage report of a run |
| `gen-e2e-shards` | the process plan of the headless Avalonia E2E suite |
| `gen-seam-graph` | the committed seam graph still matches the tree |
| `go-quality` | the Go sources of the gates: formatted, vet-clean, and registered |
| `measure-build-cpu` | what a build command costs: the wall time and the CPU utilisation |
| `measure-project-graph` | the shape of the project dependency graph |
| `msbuild-evaluation-cost` | what a Release compile spends on MSBuild project evaluation |
| `normalize-mtimes` | the source mtimes tell the truth, so MSBuild can skip what did not change |
| `pack-validate` | every packed Harbor.* package carries its metadata, its README and its lib |
| `run-e2e-avalonia-shards` | the headless Avalonia E2E plan, one process per shard |
| `test-census` | the per-test and per-project wall census of a CI run |
| `test-measurements` | the per-test measurements a CI run printed |
| `test-skips` | the tests a run skipped, and the reason each declared |
