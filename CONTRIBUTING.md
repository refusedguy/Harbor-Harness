# Contributing

Harbor is a modular .NET 10 AI coding harness. Before changing anything, read
[`AGENTS.md`](AGENTS.md) — it is the operational guide, and
[`CLAUDE.md`](CLAUDE.md) carries the code conventions it cross-references.

## Build and test

```bash
dotnet build
```

Tests run as **plain executables** — that is the form every CI job executes. Run
one project at a time:

```bash
dotnet run --project tests/<Project> -c Release --no-build -- --minimum-expected-tests 1
```

TUnit filters use `--treenode-filter` (forwarded after `--`), not `--filter`.

### Why not `dotnet test`

`global.json` selects `Microsoft.Testing.Platform` as the `dotnet test` runner,
which changes what that command accepts. Two real breakages, which earlier
versions of this file conflated into a single wrong explanation:

- **VSTest-era options are rejected.** `--logger` and `--filter` are not
  recognised by the MTP runner and come back as *invalid command-line
  arguments* — exit code **5**. A run that genuinely discovers no tests exits
  **8**. `CHANGELOG.md` (sprint *ci-cd-maturity*) records that removing a single
  `--logger` flag turned `renderer-perf-gate.yml` green.
- **11 of the 36 test projects still reference `Microsoft.NET.Test.Sdk`**
  (pinned in `Directory.Packages.props:71`). TUnit's installation docs state
  that package must not be used with TUnit because it stops test discovery.
  `TUNIT_MTP_AUDIT.md` proposes removing it; that change has not landed.

Because of the second point the earlier claim that `dotnet test` "discovers zero
tests repo-wide" cannot be right as written — 25 of the 36 test projects never
reference the package blamed for it. No CI job has run `dotnet test` since, so
its current per-project behaviour is **unverified** rather than known. That is
why the plain-executable form above is the only one documented, and why you
should not read `dotnet test` as "safe, just undocumented".
Run [`tests/Harbor.Architecture.Tests/`](tests/Harbor.Architecture.Tests) after
**every** project-reference change — the layering matrix is enforced there and
nowhere else.

Documentation is gated separately from code: `ci.yml` ignores `**.md` and
`docs/**` on purpose, so a docs-only change runs
[`tools/check-md-links.py`](tools/check-md-links.py) and
[`tools/md-lint.py`](tools/md-lint.py) instead. Both must pass:

```bash
./tools/check-md-links.py
./tools/md-lint.py
```

## Do not edit generated artifacts

Two kinds of file in this repository are outputs, not sources. Changing them by
hand breaks the pipeline that produces them, and because they are binaries the
damage shows up later as a merge conflict rather than as a failing build.

- **Demo GIFs** — `assets/demo/*.gif` and `assets/demo/baseline.json`, recorded by
  [`demo-gifs`](.github/workflows/demo.yml) from the tapes in `demo/`. The
  workflow regenerates them on a push to `dev`/`master` and commits them back;
  it does not run on pull requests. Change the tape or the renderer, push, and let
  the workflow do it.
- **Golden baselines** — `*.golden.txt` and `*.verified.*`, regenerated on demand
  by [`goldens`](.github/workflows/goldens.yml), which runs the CellForge golden
  suites with `HARBOR_UPDATE_GOLDENS=1` and commits the baselines back to the same
  branch.

Neither artifact is ever regenerated "to make a test pass". Both gates are
reporting real change; a red run means the recorded output moved, and the question
to answer is whether it was *supposed* to.

The full procedure — how to tell an intended re-record from a foreign one, what to
put in a pull request, and what the drift gate compares — is in
[`docs/DEMO_GIFS.md`](docs/DEMO_GIFS.md). Read it before regenerating a demo GIF.

## Before you open a pull request

[`docs/DEVELOPMENT.md`](docs/DEVELOPMENT.md) has the full checklist, including the
principles gate. The short version:

- `dotnet build` succeeds with zero warnings — warnings are errors here, and
  `#pragma warning disable` is not the answer.
- Every affected test project passes, run individually as shown above.
- A new tool declares its `ToolSafetyProfile`, has a permission rule in
  `PermissionRuleset.Default`, and has its own test file.
- New or moved markdown links resolve: run both doc gates.
- Your commit touches only what it claims to. Use explicit paths in `git add`;
  never `git add -A`.

Reference material: [`docs/PATTERNS.md`](docs/PATTERNS.md) for the pattern
catalogue, [`docs/ANTIPATTERNS.md`](docs/ANTIPATTERNS.md) for what is forbidden,
[`docs/CODE_PRINCIPLES_AUDIT.md`](docs/CODE_PRINCIPLES_AUDIT.md) for known
violations not to repeat, and [`docs/ROADMAP.md`](docs/ROADMAP.md) for what is
planned next.
