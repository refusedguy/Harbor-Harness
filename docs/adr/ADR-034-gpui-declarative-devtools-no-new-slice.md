# ADR-034: gpui declarative devtools report (#2700), no new slice

> **Status (2026-11-18):** dated record, not normative. Product code, tests and
> gates are unchanged by this PR. Facts were checked with `gh pr list --search`
> and an inventory of `origin/dev` (`09a673bc`). The document has no
> `Status: normative` banner, so its file references are not machine-checked.

## Status

**Decided:** no code slice for #2700. Every executable idea in the report is
already merged, in flight in another PR, or rejected as not applicable to a
terminal DSL. Issue #2700 stays open as a comparison record.

## Context

Issue #2700 is a self-contained report on devtools for declarative UI, read from
local clones of gpui-kit (`249a963`) and Zed (`6983685`). Section 4 names eight
ideas worth borrowing for Harbor DSL and five that do not transfer. The report
also asks to check applicability to the dev-only tools in PRs 2727, 2761, 2765
and 2760 and in issue 2438. A `gh pr list --search 2700` check found no PR for
this report, so this PR does not duplicate work.

| # | Idea from the report | State on `origin/dev` | Evidence |
|---|---|---|---|
| 1 | Description IR (`SpecArena`, `SpecNode`, `SpecOp`, single-use flags) | merged | PR #2489; `SpecArena.cs`, `SpecNode.cs`, `SpecOp.cs`, `SpecNodeReuseException.cs` under `src/Harbor.Abstractions.Contracts/Dsl/` |
| 2 | Describe once, replay many frames (`RenderSnapshot`) | merged | PR #2727 retains the tree and patches values in place; PR #2789 adds the dev `BuildRoot` override with a host-safe swap policy |
| 3 | Clean `Spec -> element` generator (`materialize.rs`) | merged | PR #2531 emits `BuildRoot`; PR #2535 registers panels by descriptor instead of a VM type switch |
| 4 | Slot discovery by sentinel arguments (`template.rs`) | owner decision | No runtime template body exists in the compiled C# DSL. Rationale below |
| 5 | Dev reload pipeline from `watch.rs` | merged, one slice in flight | PR #2727 (template diff, patch, rebuild, restart) and PR #2789 (swap gate); `DslDevFileWatcher.cs` is on dev; PR #2765 adds debounce, burst coalescing and atomic publication and is OPEN, so this PR does not touch it |
| 6 | `check`, `types`, `--print-spec` tooling | merged | PR #2538 adds `dsl check --print-ir` and `dsl types` in `CafDslVerbs.cs` |
| 7 | `debug_tree` format for checks without a GPU | merged | PR #2528 adds `SpecTreeDumper.cs`; PR #2761 adds `getTree` to the automation protocol |
| 8 | Zed `ElementIdStack` prefix hashes and typed element state | merged | PR #2442; ADR-030 records the same finding for report #2707 |

The dev-only tools named in the report map to the same table. #2727 is idea 2
and part of idea 5. #2761 is idea 7 plus the `clock` and `testId` commands.
PR 2760 put the watcher and interpreter diagnostics on a file-backed channel
outside the cell grid. Issue 2438 is behind PR 2761. PR 2765 is the one open
slice, and another agent owns it.

### Why sentinel slot discovery is an owner decision

gpui-shell executes a template body once with sentinel arguments and treats the
positions where a sentinel lands as slots. Harbor compiles `.harbor-ui` markup
to C# at build time through a Roslyn source generator. Dynamic values are
explicit `{Binding Path}` and `state:` directives, checked by diagnostics
HUI002 through HUI005. There is no template body evaluated at runtime, so a
sentinel pass has nothing to execute. gpui-shell's rules for that trick, no
formatting of an argument, no branching in the body and no nested views, are
constraints Harbor's compiled bindings do not carry. Adding a runtime sentinel
pass would put an interpreter into a dev-only toolchain that today ships no JIT
and is compiled out of AOT by `HarborWithDslDev=false`. Keep explicit bindings.

### What the report rules out

gpui-shell rejects markup and JSX because a second parser would cost its
"edit, save, see" loop. Harbor already has a compile step, so that argument does
not apply, but gpui-shell has no markup to copy anyway. Zed's `subsecond` and
`dx serve --hot-patch` replace machine code through a dev server and are bound
to debug builds on four desktop operating systems. The only part that transfers
is a refresh that keeps entity state, which PR #2727 covers by retaining the
tree. The QuickJS `?v=N` suffix exists because ES modules cannot be unloaded;
collectible `AssemblyLoadContext` is banned under AOT. The capability gate,
sandbox and frozen prototypes belong to a general script runtime, not to a dev
preview tool. JS builder chains already have a C# equivalent in the generated
`BuildRoot`.

## Decision

Product code is not written. Ideas 1, 2, 3 and 5 through 8 are not reimplemented:
a second IR, generator or watcher would create a second owner where the tree has
one. Idea 4 is deferred to the owner with the cost stated above. The five ideas
the report lists as not applicable stay rejected. Gates, thresholds and
self-tests are neither weakened nor added.

## Consequences

- Only two markdown files change: this ADR and [DECISIONS.md](DECISIONS.md).
  The documentation gates in `docs.yml` are the only CI risk, and both
  `check-md-links` and `md-lint` run before the PR.
- No new guard is added. On a tree where the executable ideas are merged or
  deferred, a guard would be vacuous or red on day one.
- Issue #2700 remains OPEN. It is a report, not a single-task tracker.
- Review happens on a new fact, such as a compiled-template design that needs
  runtime slot discovery, or a new devtool idea with a concrete terminal
  analogue. In either case open a new issue and ADR instead of reopening #2700.

## References

- Issue #2700, full report on gpui declarative devtools.
- Merged PRs #2442, #2489, #2528, #2531, #2535, #2538, #2727, #2761, #2789;
  open PR #2765, which this PR does not duplicate.
- [ADR-028](ADR-028-avalonia-declarative-xaml-dsl-verdict.md),
  [ADR-030](ADR-030-gpui-report-patterns-already-landed.md) and
  [ADR-033](ADR-033-declarative-ui-crosslang-report-verdict.md), the same verdict
  shape for other declarative UI reports.
- [DECISIONS.md](DECISIONS.md).
