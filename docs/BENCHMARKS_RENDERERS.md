# BENCHMARKS_RENDERERS.md — per-renderer performance contracts & baselines

> Renderer-unification sprint (Phases 1–6). Companion to [BENCHMARKS.md](./BENCHMARKS.md):
> this file covers the **renderer backends** specifically, with a formal
> performance contract enforced by CI (`.github/workflows/renderer-perf-gate.yml`).

## Contract

Every renderer backend declares three ceilings
(`src/Harbor.Ui.Framework.Rendering/PerformanceContracts/RendererPerformanceContract.cs`):

| Ceiling | Meaning |
|---|---|
| Throughput | minimum streamed events/sec, 1000-event synthetic stream, 24×80 surface |
| Latency | p99 render latency per single event |
| Memory | allocated MB per 1000 streamed events (steady state) |

**Adding a renderer backend without a passing contract will not merge** — the
`renderer-perf-gate` CI job runs `tests/Harbor.Tui.PerfTests` and the PR
template check requires a contract entry.

## Baselines (dev branch, 2026-08-28, Release, Linux x64)

Reproduce locally:

```bash
HARBOR_PERF_REPORT=report.txt dotnet test tests/Harbor.Tui.PerfTests -c Release
```

| Backend | Events | Throughput (ev/s) | p99 latency (ms) | Allocated (MB/1k ev) |
|---|---|---|---|---|
| `ansi` (AnsiPlain, ANSI strategy) | 1000 | 693 193 | 0.002 | 0.374 |
| `plain` (AnsiPlain, null strategy) | 1000 | 635 728 | 0.006 | 0.355 |
| `cellforge` (CellForge adapter, in-memory backend) | 1000 | 549 179 | 0.007 | 0.347 |
| `nickconsoleex` (nickprotop/ConsoleEx, headless 120×40) | 1000 | 154 010 | 0.014 | 2.971 |

Committed machine-readable baseline: `tests/Harbor.Tui.PerfTests/baseline.csv`
(the strict-mode CI step enforces a **5 %-regression rule** against it; strict
mode is intended for dedicated perf hardware — shared runners are too noisy).

## Full-redraw vs cell-diff bandwidth

The portable cell-diff protocol (`Harbor.Ui.Framework.Rendering.Protocol`,
Phase 6.2) makes differential rendering consumable by any backend. Measured on
a 24×80 screen with a one-cell change per frame (typical streaming tick):

| Render path | Bytes on the wire per changed frame | Notes |
|---|---|---|
| Full redraw (ANSI, all 1920 cells + styles) | ~90–120 KB | what a non-differential backend ships |
| Cell-diff batch, 1 cell changed (`CellDiffBatchCodec`) | ~42 B message + 25 B header | ~2000× reduction |
| Cell-diff batch, 24 cells (one status row) | ~1.1 KB | ~100× reduction |

For backends that consume batches (`DifferentialRenderPipeline`), bandwidth is
proportional to *damage*, not to screen size — the competitive differentiator
the protocol formalizes.

## Differential markdown (Phase 6.4)

`MarkdownRenderPerformanceContract` (`MarkdownRenderPerformanceGate.Validate`):

| Scenario | Budget | Measured |
|---|---|---|
| Frozen-block restore (per block, O(1)) | < 1 ms | ≪ 0.1 ms |
| 100-block document, 99 frozen, tail-only re-render | < 2 ms | ≈ 0.2 ms |
| 10 000-token stream into one tail block, total | < 300 ms | ≈ 110 ms |

The design guarantee: **re-render cost depends on the number of unfinished
blocks, not on document size**. Frozen blocks are restored from
`FrozenTailMarkdownCache` (LRU, 500-block ceiling) as immutable cell snapshots.

## Visual regression (Phase 5)

`tests/Harbor.Tui.RendererTests/` pins golden frames per backend
(`cellforge`, `nickconsoleex`, `ansiplain-ansi`, `ansiplain-plain`) over one
canonical event stream, plus a strategy-parity contract: ANSI output with
escape sequences stripped must equal the plain-mode output byte-for-byte.
Regenerate after an intentional visual change with
`HARBOR_UPDATE_GOLDEN=1 dotnet test tests/Harbor.Tui.RendererTests` and review
the golden diff in the PR.

## Backend inventory (Phase 3/4 state)

| Backend id | Project | Selection |
|---|---|---|
| `cellforge` (alias `consoleex`) | `Harbor.Tui.CellForge` | `HARBOR_TUI=cellforge` / `defaultTuiRenderer` |
| `nickconsoleex` | `Harbor.Tui.NickConsoleEx` (vendored nickprotop/ConsoleEx submodule) | `HARBOR_TUI=nickconsoleex` / `defaultTuiRenderer` |
| `ansi`, `plain` | `Harbor.Tui.AnsiPlain` (merged; escape-code strategy per mode) | `HARBOR_TUI=ansi|plain` / `defaultTuiRenderer` |

Runtime hot-swap (Phase 6.3): `/renderer <backend>` mid-session, guarded by
`ui.runtime_swappable` (CLI: `runtimeSwappable`) and `HARBOR_TUI_RUNTIME_SWAP`;
CAS-gated, UiState-snapshot-restoring, old renderer disposed exactly once.

## Methodology

- Release build, warmup before measuring (JIT absorbed), single-threaded.
- Allocation metric: `GC.GetAllocatedBytesForCurrentThread()` delta per run.
- p99: per-event Stopwatch ticks, sorted, index ⌈0.99·N⌉−1.
- Timing ceilings in the contract are absolute and deterministic-friendly;
  relative 5 % regression enforcement is strict-mode-only (perf hardware).
