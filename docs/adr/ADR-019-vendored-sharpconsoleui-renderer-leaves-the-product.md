# ADR-019: the vendored SharpConsoleUI renderer leaves the product

> **Status (2026-10-20):** dated record, not normative. The decision this file
> records was taken by the owner on 2026-10-20. It asserts no machine-checkable
> fact about the tree as it stands. The parts that must stay true are fenced
> where they live: the backend registry in
> `src/Harbor.Hosting/Modules/TuiBackendRegistry.cs` and the layer matrix in
> `tests/Harbor.Architecture.Tests/FullLayerMatrixTests.cs`.

## Status

**Decided: the vendored SharpConsoleUI renderer leaves the product.** Date of
decision: 2026-10-20. The owner's words, on issue
[#1921](https://github.com/refusedguy/Harbor-Harness/issues/1921): *"убрать
полностью в контриб, кто захочет - пусть и дрочится"* (move it out to
`contrib/` entirely; whoever wants it can wire it up themselves).

The decision is not recorded in a comment on that issue. This file is the
durable place to point at, so that the next reader does not have to
reconstruct the reasoning from a thread.

The tree change that carries the decision lands in a separate change. This file
records the decision and the reasoning behind it.

## Context

Harbor shipped two interactive terminal renderers, plus the ANSI and plain
streaming pair around them. CellForge is the canonical interactive one and does
the work. The second, `Harbor.Tui.NickConsoleEx`, was a thin wrapper over
SharpConsoleUI, a third-party terminal UI kit that Harbor vendored as a git
submodule at `external/ConsoleEx` (a fork of
`github.com/nickprotop/ConsoleEx`).

The wrapper itself was thin in a way that mattered. It rendered through
SharpConsoleUI's `MarkupControl` and used none of the kit's window, panel or
theme system. The project's own plan recorded the renderer as underutilized on
exactly that point.

The cost of carrying it was measurable and landed on every build.

Issue #1921 read the compile timeline of Release build 39389070999 and found
that **three of the 110 projects were compiled in Debug inside the Release
build**, and that no Release output of them exists:

| Project | Closed its window | What landed in the artifact |
|---|---:|---|
| `src/Harbor.CodeGen` | +69.54 s | nothing in `src/Harbor.CodeGen/bin/` |
| `external/ConsoleEx/SharpConsoleUI` | +214.47 s | nothing in `external/ConsoleEx/SharpConsoleUI/bin/` |
| `apps/Harbor.App.Avalonia` | +260.82 s | nothing (excluded on purpose) |

Two facts follow from that table for this decision.

1. **The vendored tree cost a 28.3 s window in every Release build.** The issue
   states the numbers as gaps in the timeline rather than as pure compile time
   (69.54 s for CodeGen, 28.3 s for SharpConsoleUI), and it names the third
   project as the one whose Debug output is excluded on purpose. A third-party
   UI kit that Harbor does not use for its main render path was on the critical
   path of every Release compile.
2. **The vendored tree's Debug output landed in the shipped CLI.** The Release
   build produced no Release build of SharpConsoleUI, so
   `apps/Harbor.App.Cli/bin/Release/net10.0/` carried a Debug build of a
   third-party UI kit into the release artifact. The issue calls that out as
   both size and behaviour.

This decision fixes one of the three projects in #1921. `Harbor.CodeGen` and
`apps/Harbor.App.Avalonia` are out of scope here and stay open in that issue.

The vendored tree also carried a maintenance debt of its own, recorded in
[ADR-012](ADR-012-vendored-html-collapse-whitespace-pair.md). It is a git
submodule of a third-party repository, so a fix to it has no Harbor remote to
go to. It also carried a second copy of a whitespace-collapse helper that
diverges from Harbor's own copy: on an exhaustive alphabet the two disagree on
758 of 781 strings (97.1 %). Both copies are private, and neither is reachable
from a user path.

## Decision

Remove the renderer from the product. Move both the wrapper and the vendored
tree under `contrib/`, under the terms `contrib/` already carries.

### What leaves the product

- The `HARBOR_WITH_NICK_CONSOLE_EX` build constant and every code path behind
  it.
- The `nickconsoleex` value of the build-time `HarborWithRenderer` selection.
  The remaining values are `all`, `cellforge` and `ansiplain`.
- The `tui` configuration value `nickconsoleex`, and the `HARBOR_TUI=nickconsoleex`
  environment selection. Both stop selecting anything.
- `BackendKind.NickConsoleEx`, a member of the public `BackendKind` enum in
  `Harbor.Terminal.Abstractions`. **This is a breaking public API change.** A
  consumer that switches over the enum exhaustively has to handle the shorter
  list.
- The `Harbor.Tui.NickConsoleEx` NuGet package. The project left
  `Harbor.slnx`, and the repo's pack target packs the solution, so the package
  is not produced any more.

### What moves to `contrib/`

| From | To |
|---|---|
| `src/Harbor.Tui.NickConsoleEx/` | `contrib/tui/Harbor.Tui.NickConsoleEx/` |
| `external/ConsoleEx/` (git submodule) | `contrib/external/ConsoleEx/` |
| `external/Directory.Build.props` | `contrib/external/Directory.Build.props` |
| `external/Directory.Build.targets` | `contrib/external/Directory.Build.targets` |
| `external/Directory.Packages.props` | `contrib/external/Directory.Packages.props` |

The three boundary files travel with the submodule because they exist to stop
the root build props from leaking into third-party source: they keep Central
Package Management, `TreatWarningsAsErrors` and the root `Spectre.Console` pin
out of the vendored tree, and the `Directory.Build.targets` override is what
retargets SharpConsoleUI's `Spectre.Console >= 0.55.0` edge onto the Harbor
pin.

`contrib/` already has terms, and this move does not relax them:

- **Unmaintained.** Nobody works on what is in `contrib/`.
- **Not compiled by CI.** No CI job builds it, so breakage there is invisible
  to CI and must be found by the person who wants it.
- **Not in `Harbor.slnx`.** The product's solution does not list it. `contrib/`
  has its own solution file (`contrib/Contrib.slnx`) for the components it
  does build.
- **No `ProjectReference` from `src/` or `apps/`.** Nothing in the product
  depends on it.
- **Not published.** The project's `PackageId` is still declared in its csproj,
  but it is in no solution, so the repo's pack pipeline does not produce the
  package.

### What a user who wants this renderer must do now

Plainly: the product does not ship it any more, and no configuration value
brings it back.

To get SharpConsoleUI-backed rendering, a user now has to do the work the
project used to do for them:

1. Clone the vendored tree themselves (the submodule moves to
   `contrib/external/ConsoleEx` and is not fetched by a normal clone).
2. Build the wrapper themselves, against their own copy of the vendored tree.
3. Wire the result into a Harbor build by hand, since nothing in the product
   references it any more.

A config that still names the old renderer does not select one, and it does
not fail loudly either.

- `HARBOR_TUI=nickconsoleex` and `"defaultTuiRenderer": "nickconsoleex"` in
  `~/.harbor/cli.json` both reach the backend registry. The id is not in it,
  so it resolves to the fallback backend the build ships with (`plain`, or
  `ansi` in a build that has the Spectre shells compiled in) and Harbor logs
  one warning: `Unknown HARBOR_TUI 'nickconsoleex'; falling back to 'plain'`.
- `"tui": "nickconsoleex"` in `~/.harbor/config.json` was already inert before
  this decision, because that file's `tui` value is honoured only when it
  names CellForge. It stays inert.
- The config normalizer does not reject the value. It copies `tui` verbatim,
  and the only validation on it is that it is not empty. The config file still
  loads.

## Consequences

- **The release artifact is smaller and the Release compile is shorter.** The
  vendored tree is no longer on the critical path of a Release build, and its
  Debug output is no longer part of the shipped CLI.
- **Two renderers ship.** CellForge is the canonical interactive backend and
  AnsiPlain covers ANSI streaming, plain pipes and CI. `HARBOR_TUI` values
  that name a backend which is not compiled in fall back the same way they
  always did.
- **`BackendKind` loses a member.** Consumers of the public enum that switch
  over it exhaustively have to handle the shorter list.
- **The vendored tree keeps rotting on its own.** It is a submodule pinned to a
  third-party fork, unbuilt by CI, and nobody will move the pin. A user who
  takes it on takes that on too.
- **#1921 is one third done.** The `Harbor.CodeGen` and
  `apps/Harbor.App.Avalonia` halves of the Debug-in-Release defect stay open.

## Alternatives considered

- **Keep the renderer, fix the Debug-in-Release build.** This fixes the
  symptom the issue measured and leaves the cost in place: a third-party UI kit
  Harbor does not use for its main render path stays on the critical path of
  every Release compile, with the submodule pin and the merge debt that comes
  with it.
- **Keep the renderer and build it from the NuGet package instead of the
  submodule.** The vendored tree exists because the fork carries a patch Harbor
  needs. A package feed would mean owning the patch as a published artifact
  and carrying a second release train for it.
- **Delete the vendored tree outright.** The owner chose `contrib/`, so the
  tree stays readable and forkable in the repository, and the cost of keeping
  it is bounded by the fact that nothing builds it.
