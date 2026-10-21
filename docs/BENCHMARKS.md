# Benchmarks — Harbor

> **Sources.** Numbers below come from two environments — do not compare raw values across them:
> - **CI-short** — PR `benchmark` job (`.github/workflows/benchmark.yml`, `ubuntu-latest`, `taskset -c 1`,
>   `--job Short`). Three runs on three different runners, so do not compare raw means across them:
>   2026-09-09 (AMD EPYC 9V74, .NET 10.0.12, BDN 0.15.8) for `*PermissionRuleset*` + `*Registry*`;
>   2026-09-28 (AMD EPYC 7763, .NET SDK 10.0.401, runtime 10.0.12, BDN 0.15.8) for
>   `*EventBusBenchmark*` (§5.4, #391). The allocation column is runner-independent; the
>   time column is not. Marked **[CI-short]** in the tables.
>   `*PermissionRuleset*` + `*Registry*` match exactly three classes: `PermissionRulesetBenchmark`,
>   `ProviderRegistryBenchmark`, `ToolRegistryBenchmark`. `*EventBusBenchmark*` matches two more:
>   `EventBusBenchmark` and `EventBusBenchmarkFanout`. No other row in this file is produced by that
>   job — in particular the `*Delivery*` rows of §5.3 are still code-only, the job does not run them.
> - **Local full runs** — 2026-08-22 (i5-8250U, .NET 10.0.10) plus UiStore/streaming rows from 2026-09-10
>   (machine n/a). Since #46 all classes use unified `[SimpleJob(warmup 3 / iter 5)]`; older rows were
>   measured with mixed configs (2/3 or 3/10), so absolute values will shift on re-measure.
> - **CI test shards** — the `test` job of `.github/workflows/ci.yml` on `ubuntu-24.04`, SDK 10.0.402,
>   Release binaries, one shard per job (§6.1, §6.4). Every row carries its run id; the runs of
>   2026-10-20 are the ones quoted. A shard wall is a wall on a shared runner, so the same shard
>   moves by 10-30 % between runs: compare a run with a run, never with a local number.
> - **⏳ not yet measured** — the benchmark exists in `tests/Harbor.Benchmarks` but has no run recorded
>   here yet. Never invent a value for these; run the row locally and fill it in with machine + date.
>   Introduced by the #408 bus split, whose rows are code-only until the next local pass.
>
> **How to read any row in this file:** it lives under a section header that names the machine, the
> date and the job, or it carries an explicit `⏳ not yet measured` / `⚠️ retracted` marker. There is
> no unattributed row — that is the litmus for #408.
>
> Suite: `tests/Harbor.Benchmarks` — 43 benchmark classes (counted 2026-10-11; the number grew
> with the #408 split, the #391 EventBus rewrite and the #1239 `DictionaryPoolBenchmark`),
> `[MemoryDiagnoser]`, Release, 0 warnings.
> Since #408 every class carries its own 7-field measurement contract (Operation / Payload /
> StateReset / Drain / RetainedState / AwaitSemantics / AllocAttribution), enforced by
> `BenchmarkContractTests` in `tests/Harbor.Architecture.Tests`. Read the class doc before quoting its number.
> Run: `dotnet run -c Release --project tests/Harbor.Benchmarks -- --filter "*<Category>*" --buildTimeout 600 --keepFiles`

## Bottlenecks (P0→P3, measured)

> **Provenance.** Rows that name a date inline are attributed there. Rows without a date are local
> full runs (2026-08-22, i5-8250U, .NET 10.0.10, Release) as tabulated in §5.2 — except the
> `EventBroadcaster` row, which is an **undated pre-#408 measurement** and must be re-measured before
> it is quoted again. None of these rows is produced by the CI-short job (`*PermissionRuleset*` /
> `*Registry*` filters do not match these classes).

| # | Target | Evidence | Fix direction |
|---|---|---|---|
| ~~P0~~ | ~~`AppReducer` streaming concat~~ **withdrawn #594** | 1.72 ms / **19.4 MB** per 1000 TextDelta (O(N²) string +) | *measured on `Harbor.Ui.Framework.Reducers.AppReducer`, deleted in #594 as dead — no composition root ever fed its store. The O(N²) defect it exhibited was fixed for real in the live path by `ChunkedBuffer` / `StreamingCoalescer`; see the `StreamingCoalescer` row and the `UiStore dispatch` rows below. The benchmark row is gone with the code, the defect is not back.* |
| P0 | `MessageConverter` large msgs | serialize 2.35 ms / 1.2 MB per msg; 100×large round-trip **545 ms** | Utf8Json source-gen (audit §PERF-002) |
| P1 | `CompactionService.ShouldCompact` | 598 µs @1000 msgs **каждый turn** | incremental token counter |
| P1 | `EventBroadcaster` | 9–11 ms / **8 MB** per 1000 events, не зависит от числа клиентов ⚠️ undated pre-#408 run | serialize once, reuse buffers |
| P1 | `EventBus.PublishAsync` | ~~фикс. 8.1 KB alloc даже при 0 подписчиков~~ ✅ resolved: 0-sub fast path returns before scrollback/fan-out (**measured 8.15 ns / 0 B** by #391, §5.4; zero alloc locked by `PublishAsync_ZeroSubscribers_IsAllocationFree`, reachability by `EventBusFastPathTests`); 1/10-sub fan-out covered by bounded tripwires (#186) | ring-buffer scrollback (landed) |
| P2 | `StreamingCoalescer` tool-call Materialize | 481 µs @1000 дельт (35–48× медленнее текста) | кэш разобранных аргументов |
| P2 | `PatchTool` apply | 10.1 ms / **9.3 MB** @5000 hunks | стримить вместо List<string>+Join |
| P2 | `DefaultUiProjector` | 20.8 ms @5000 строк за кадр (холодный полный проход; инкрементальный кэш уже влито — см. ниже) | инкрементальная проекция по revision |
| OK | `DefaultUiProjector` инкремент | 1000 дельт → 1001 проекция: **962 no-op reuse, 38 tail (флаши), 1 history**; markdown-парсов на store-пути 0; итого 475 µs / 1.03 MB (2026-09-10, `StreamingDeltaFrequencyBenchmark`, регрессия — `StreamingFrequencyTests`) | пожара нет; следить за transcript-композицией при росте history. С 2026-10-01 абсолютная цифра перестала быть гейтом: **#410** закрыл путь `UiStore.Dispatch` + `DefaultUiProjector` относительными гейтами в `test (ui)` — см. §5 «Store-path scaling gates (#410)» |
| OK | «markdown-парсов на store-пути 0» — **больше не только проза** | 200 дельт через `UiStore.Dispatch` + `DefaultUiProjector.Project` → `UiStageCounters.MarkdownParses == 0`; контроль в том же файле требует 1 при реальном парсе, так что 0 — измерение, а не тавтология (`StorePathSkipsMarkdownParseTests`, #409, 2026-10-01) | клетка проверяется, а не цитируется; **#410** тем временем считает ту же нулевую форму прямо в своём скрипте, без продуктового счётчика — см. §5 ниже |
| P3 | `SessionId` Dictionary key | медленнее string (7.9 vs 6.3 µs), HashSet быстрее — проверить GetHashCode | override hash |
| P3 | `OpenAiWire.TryParseChatChunkLine` | плоские ~10 µs floor на любой чанк | Utf8JsonReader поверх span без ToString() |
| OK | `StatusBarLayout.Fit` (per painted frame) | ✅ resolved: O(n²) width lookups under a process-global monitor → **exactly one lookup per segment, per-thread cache, no lock** (#487, §5.6). Machine-independent count, stopwatch rows pending a BDN run | — |

## Key numbers — local full runs (2026-08-22, i5-8250U, Release JIT; UiStore streaming rows 2026-09-10, machine n/a)

| Operation | Mean | Allocated |
|---|--:|--:|
| AgentLoop turn (no tool) | 10.2–11.8 µs | 5.5 KB |
| AgentLoop turn (+tool) | 16.6–26.6 µs | 7.5 KB |
| EventBus.PublishAsync (0 sub) ⚠️ retracted, pre-ring — §5.4.4 | 8.1 µs | 8.1 KB |
| WireCodec roundtrip 64B / raw frame 64B | 6.2 µs / 0.25 µs | 1.8 KB / 0 |
| OpenAiSse.ParseChunk 32B→4KB | 10.2–10.6 µs | 3.9–6.8 KB |
| JsonlSessionStore.Append ×100 | 1.74 ms | 187 KB |
| Sqlite WAL Append ×10 | 2.2–2.6 ms | 155 KB |
| ~~AppStore.Dispatch TextDelta ×1000~~ **withdrawn #594** — measured on deleted `Harbor.Ui.Framework.Reducers.AppStore`, see the Bottlenecks table | ~~1.72 ms~~ | ~~19.4 MB~~ |
| UiStore dispatch + DefaultUiProjector per delta ×1000 (24B deltas, 2026-09-10) | 475 µs | 1.03 MB |
| UiStore dispatch + DefaultUiProjector per delta ×2000 (24B deltas, 2026-09-10) | 1.09 ms | 2.46 MB |
| DefaultUiProjector 5000 lines | 20.8 ms | ~MB |
| Terminal ANSI vs plain blit | 364 / 330 µs | 12 / 10 KB |
| PatchTool apply 5000 hunks | 10.1 ms | 9.3 MB |
| Identifiers: HashSet<SessionId> vs string | 2.1 vs 2.7 µs | 2.3 vs 7.3 KB |
| SystemPromptBuilder (16 tools, large) | 3.8 µs | 12.1 KB |
| StateDiff Record.Equals identical | 0.59 ns | 0 |

## Key numbers — CI-short **[CI-short]** (PR `benchmark` job, `--job Short`, ubuntu-latest, 2026-09-09)

| Operation | Mean | Allocated |
|---|--:|--:|
| PermissionRuleset.Evaluate (default Allow) | 0.35 µs | 0 |
| PermissionRuleset.Evaluate (Deny bash rm -rf /) | 0.17 µs | 488 B |
| ToolRegistry.ResolveTools frozen @4 (no permission) | 0.085 µs | 344 B |
| ToolRegistry.ResolveTools frozen @4 (with permission) ⚠️ retracted, see §5.1 | 2.3 µs | 88 B |
| ToolRegistry.ResolveTools frozen @8 / @16 (no permission) | 0.16 / 0.31 µs | 664 B / 1304 B |
| ToolRegistry.GetTool (frozen) | 0.10–0.20 µs | 80–160 B |
| ProviderRegistry.GetClient frozen | 0.14 µs | 288 B |
| ProviderRegistry.GetAllModelsAsync frozen @1 / @5 / @20 providers | 9.2 / 12.9 / 24.6 µs | 1112 B / 2776 B / 9016 B |

---

> **Historical measurements (2026-07-18)** taken on the actual codebase with .NET 10.0.302 SDK.
> Previous versions of this doc contained inflated numbers (5 MB binary, 28 MB RSS) — those were Debug JIT DLL sizes and debug-process RSS. This version measures what users actually see.

## 1. Environment

| Property | Value |
|---|---|
| OS | Linux 6.x (container) |
| Architecture | linux-x64 |
| .NET SDK | 10.0.302 |
| .NET Runtime | 10.0.10 |
| CPU | shared vCPU (cloud sandbox) |
| RAM | 16 GB |
| Disk | SSD (cloud sandbox) |
| Harbor commit | post-architecture-cleanup (round 5) |
| Date | 2026-07-18 |
| Build config | Release (`-c Release`) |

> Numbers are **relative indicators**, not absolute promises. Production hardware will differ. The cloud sandbox CPU is variable — cold start in particular fluctuates ±200 ms between runs.
>
> **Scope of this section:** every table in §2, §3 and §4 below is measured in *this* environment
> (2026-07-18, linux-x64 container, .NET 10.0.302 Release) unless the table itself says otherwise —
> §5, §6 and §7 each carry their own machine/date or job attribution.

## 2. Solution metrics

### 2.1 Project count

| Category | Count |
|---|---:|
| App projects (`apps/`, на диске) | 2 (`Harbor.App.Cli`, `Harbor.App.Avalonia`; WPF/MAUI/Blazor переехали в `contrib/apps/`) |
| Source projects (`src/`, на диске) | 59 (58 в `Harbor.slnx` + `Harbor.CodeGen` build-tool вне решения) |
| Test projects (`tests/`, csproj dirs) | 44 |
| Sample plugins (`samples/`) | 4 |
| **Total in `Harbor.slnx`** | **109** (src 58 + tests 44 + apps 1 + samples 4 + build 1 + tools 1) |

> Recounted 2026-10-20 (#1884) against `origin/dev` at `2831b3ef`:
> `find src -name '*.csproj' | wc -l` → 59, `find tests -name '*.csproj' | wc -l`
> → 44, `grep -c 'Project Path=' Harbor.slnx` → 109. The previous row (52 / 37 /
> 95) was counted on the 2026-07-18 tree and had drifted; it described a
> solution with 14 fewer test projects.

### 2.2 Build time

| Step | Duration |
|---|---|
| `dotnet restore` (cold, no cache) | ~6 s |
| `dotnet build` Debug (incremental, warm) | ~25 s |
| `dotnet build -c Release` (incremental, warm) | ~75 s |
| `dotnet build -c Release --no-incremental` (full) | ~120 s |

> These four rows are LOCAL numbers from the 2026-07-18 container of §1, on one
> developer machine. The CI compile is a different animal and is measured
> separately in §6.4: the NUKE `Compile` target takes 295-434 s on
> `ubuntu-24.04`, and the spread between runs is wider than the difference
> between the lane shapes.

### 2.3 Lines of code

| Category | Lines |
|---|---:|
| Product code (`src/` C#) | ~25 000 |
| Test code (`tests/` C#) | ~12 000 |
| Documentation (`docs/`, `*.md`) | ~18 000 |
| Sample plugins | ~1 500 |

## 3. Binary sizes

### 3.1 Per-app DLL (Release, JIT)

App DLL only — not counting dependencies.

| App | TargetFramework | DLL size |
|---|---|---:|
| `Harbor.App.Cli` | net10.0 | 101 KB |
| `Harbor.App.Avalonia` | net10.0 | 207 KB |
| `contrib/apps/Harbor.App.Wpf` | net10.0-windows10.0.19041 | 125 KB |
| `contrib/apps/Harbor.App.Blazor` | net10.0 | 132 KB |
| `contrib/apps/Harbor.App.Maui` | net10.0-windows | (skeleton — TBD) |

### 3.2 Publish folder size (JIT, framework-dependent)

Includes the app DLL + all NuGet deps + runtime deps. **This is what `dotnet publish` produces.**

| App | Publish folder size |
|---|---:|
| `Harbor.App.Cli` | **109 MB** |
| `Harbor.App.Avalonia` | ~140 MB (estimated; Avalonia + Skia + AvaloniaEdit) |
| `contrib/apps/Harbor.App.Wpf` | ~120 MB (estimated; Windows-only) |
| `contrib/apps/Harbor.App.Blazor` | ~115 MB (estimated; ASP.NET Core runtime) |
| `contrib/apps/Harbor.App.Maui` | ~150 MB (estimated; MAUI workload) |

> The 109 MB CLI publish folder is dominated by `Microsoft.Extensions.*`, `Spectre.Console`, `Spectre.Tui`, `Microsoft.CodeAnalysis.CSharp` (Roslyn — 30+ MB alone for plugin compilation), and `MemoryPack` source-gen assemblies.

### 3.3 NativeAOT

**Status: publishable on the `fulltree` recipe since #1055s3** (record: `.github/aot-warning-baseline.txt`, gate: #413; measured binary + workload in §4.4). What unblocked it: the in-process CS-source plugin pipeline (`Harbor.Plugins.{Compilation,Instantiation,Registration,Storage}`) left the CLI process — the default route is the out-of-process host — so Roslyn (`Microsoft.CodeAnalysis.CSharp`) is no longer in the trim graph. No suppression was added. Still true: the `minimal` recipe (`HARBOR_MINIMAL=true`, the stripped profile) does not compile (11 x CS0234, see the baseline file), and the contrib Spectre renderers stay out of the AOT graph via build flags.

**Roadmap remainder:** the `minimal` profile plus dropping in-process Roslyn and Spectre reflection for the main process was the v0.9 two-process milestone — the Roslyn half of that landed via #1055. Measured AOT binary size: 29 MB self-contained (§4.4), against the 14–20 MB estimate below.

To reproduce the measured publish today (needs the AOT toolchain: `clang`, `lld`, `binutils`, `zlib1g-dev`):
```bash
dotnet publish apps/Harbor.App.Cli -c Release -r linux-x64 --self-contained true -p:HarborWithAot=true
# Green in CI (the `aot-publish` job); the published binary runs version/help/providers. Full recipe in tools/measure-aot-workload.sh.
```

### 3.4 Framework-dependent vs Self-contained

| Mode | CLI publish size | When to use |
|---|---:|---|
| Framework-dependent (default) | 109 MB | Dev / power users with .NET 10 installed |
| Self-contained `--self-contained` | ~85 MB + ~75 MB runtime = **160 MB** | End users without .NET |
| Self-contained + `PublishSingleFile` | ~85 MB single binary + ~75 MB runtime files | Distribution |
| Self-contained + `PublishTrimmed` | ~50 MB | Experimental — breaks Spectre.Console reflection |
| NativeAOT (`fulltree`, measured §4.4) | 29 MB self-contained | Production target |

## 4. Runtime metrics

### 4.1 Cold start

Measured as wall-clock time from process spawn (`dotnet run --no-build -c Release --project apps/Harbor.App.Cli -- --version`) to first byte of stdout.

| Run | Duration |
|---|---:|
| Run 1 | 966 ms |
| Run 2 | 887 ms |
| Run 3 | 1 032 ms |
| **Median** | **966 ms** |

Breakdown (estimated):
- `dotnet` host startup + JIT: ~300 ms
- Assembly load (`Harbor.App.Cli.dll` + deps): ~200 ms
- `HostBuilder` DI wiring: ~150 ms
- Provider/tool/agent registry construction: ~200 ms
- `IHost.StartAsync`: ~100 ms

> AOT target: <100 ms cold start (10x improvement) once NativeAOT is supported.

### 4.2 RSS (resident set size)

**Could not measure accurately in sandbox** — the `harbor ask "say hi"` command requires API key + provider, which fails in the sandbox.

Estimates based on .NET 10 baseline + Harbor deps:
- CLI idle (after `--version` exits): N/A (process exits too fast)
- CLI active (running prompt): ~80-120 MB (dominated by Spectre.Console + Roslyn plugin host)
- Avalonia app (window open): ~150-200 MB (Avalonia + Skia + AvaloniaEdit)
- WPF app: ~120-180 MB (AvalonEdit + AvalonDock)
- Blazor Server: ~100-150 MB (Kestrel + SignalR)

> The previous "28 MB RSS idle" claim was for a Debug build of the old `Harbor.Cli` (pre-split) running `--version` and exiting immediately — not a realistic number for an interactive session.

### 4.3 GC pressure

Not yet measured with `dotnet-counters`. The `InMemoryEventBus`, `UiStore` (now lock-free CAS), and `AgentLoop` (uses `StringBuilderPool`, `ArrayPool`) are designed for low allocation. BenchmarkDotNet microbenchmarks below quantify the hot paths.

### 4.4 JIT vs NativeAOT scripted workload (#411) — both halves measured

Harness: `tools/measure-jit-workload.sh` + `tools/measure-aot-workload.sh` (offline CLI verbs `--version` / `--help` / `providers`, N=7, median + spread — never a single number; same verbs, same N, same table shape); CI runner `.github/workflows/meas-jit-aot.yml` (measurement only, not a gate). Full logs are the `jit-workload-log` / `aot-workload-log` artifacts of the runs linked below.

Measured (JIT) — run [37935894413](https://github.com/refusedguy/Harbor-Harness/actions/runs/37935894413), 2026-10-09, commit `eebc9be3`, AMD EPYC 7763 (4 vCPU, x86_64), Ubuntu 24.04.5 LTS, .NET SDK 10.0.401, framework-dependent Release, apphost-driven (no `dotnet` muxer in path).

| Verb | Median | Min | Max | Spread | RSS peak (median) | RSS peak (max) |
|---|---:|---:|---:|---:|---:|---:|
| `--version` | 138 ms | 133 ms | 194 ms | 1.46x | 43 124 KB | 43 248 KB |
| `--help` | 134 ms | 131 ms | 143 ms | 1.09x | 43 336 KB | 43 336 KB |
| `providers` | 459 ms | 454 ms | 533 ms | 1.17x | 96 440 KB | 96 448 KB |

Wall = process spawn-to-exit per verb; RSS = `ru_maxrss` peak (KB). Publish dir: 51 MB (53 159 039 bytes); apphost binary: 40 MB (framework-dependent — needs an installed .NET runtime). The `providers` verb is the DI-heavy one (full `HostBuilder` + provider registry) and the closest proxy here to real startup: ~459 ms JIT, ~94 MB RSS peak.

Measured (AOT) — run [38110959202](https://github.com/refusedguy/Harbor-Harness/actions/runs/38110959202), 2026-10-11, commit `1ef2e522` (PR #1151 head; delta vs dev is harness files only, no production code), same machine class — AMD EPYC 7763 (4 vCPU, x86_64), Ubuntu 24.04.5 LTS, .NET SDK 10.0.401 — self-contained linux-x64, verbatim #413 recipe (`-p:HarborWithAot=true`; record: `.github/aot-warning-baseline.txt`, status=published).

| Verb | Median | Min | Max | Spread | RSS peak (median) | RSS peak (max) |
|---|---:|---:|---:|---:|---:|---:|
| `--version` | 18 ms | 17 ms | 25 ms | 1.48x | 29 220 KB | 29 220 KB |
| `--help` | 19 ms | 17 ms | 24 ms | 1.38x | 29 220 KB | 29 220 KB |
| `providers` | 58 ms | 55 ms | 62 ms | 1.12x | 35 544 KB | 35 544 KB |

Wall and RSS as above. Publish dir: 41 MB (41 868 731 bytes); binary: 29 MB, self-contained (no shared framework needed). Comparability note: JIT and AOT columns come from different days but the same machine class, OS, and SDK; the same dispatch re-ran the JIT leg and its wall-clock medians (139 / 133 / 420 ms) reproduce the committed JIT column within spread.

Answer to the #411 decision question, quantified: AOT buys **7.7x / 7.1x / 7.9x** spawn-to-exit on `--version` / `--help` / `providers`, **1.5x** RSS on the light verbs (43 → 29 MB) and **2.7x** on the DI-heavy verb (94 → 35 MB), with a 29 MB self-contained binary replacing a 40 MB framework-dependent apphost. The §4.1 target (<100 ms cold start) is met as a measurement on all three verbs (18 / 19 / 58 ms). On this workload the trade of #48 answers **yes**: the startup win is measured, and the thing given up for it — in-process Roslyn plugin compile — already has its default out-of-process route (#1055, 0.021 ms mean loopback dispatch). What this table still does not cover, for either mode: time-to-first-frame, steady-state allocs/op, and input-latency p50/p95 from the #411 criteria — spawn-to-exit plus RSS peak is the evidence that exists.

## 5. Microbenchmarks (BenchmarkDotNet)

Located in `tests/Harbor.Benchmarks/`. Run with:
```bash
dotnet run -c Release --project tests/Harbor.Benchmarks -- --filter '*'
```

> **Note:** CI-short rows (`--job Short`) are quick PR-gate numbers, not full BDN runs —
> expect wider error bars than the local full-run tables below.
>
> Instability watch: `Evaluate (custom ruleset, Allow at end-of-scan)` @64 rules measured 1.3–4.1 µs
> with ±7 µs error (median/mean diverge) — shared-runner noise or a pathological case; @4 rules is a
> stable 25 ns, @16 rules ~0.4 µs. Re-measure isolated before optimizing.

### 5.1 CI-short **[CI-short]** — registry + permission (PR `benchmark` job, `--job Short`, 2026-09-09, AMD EPYC 9V74, .NET 10.0.12, BDN 0.15.8)

| Benchmark | Mean | StdDev | Allocations |
|---|---:|---:|---:|
| `ProviderRegistry.GetClient` (frozen) | 0.14 µs | 0.01 µs | 288 B |
| `ProviderRegistry.GetAllModelsAsync` (frozen, 1 / 5 / 20 providers) | 9.2 / 12.9 / 24.6 µs | 0.8 / 1.8 / 6.7 µs | 1112 B / 2776 B / 9016 B |
| `ToolRegistry.ResolveTools` (4 tools, frozen, no permission) | 0.085 µs | 0.001 µs | 344 B |
| `ToolRegistry.ResolveTools` (4 tools, frozen, with permission) ⚠️ retracted | 2.3 µs | 0.02 µs | 88 B |
| `ToolRegistry.ResolveTools` (8 / 16 tools, frozen, no permission) | 0.16 / 0.31 µs | 0.001 / 0.003 µs | 664 B / 1304 B |
| `ToolRegistry.ResolveTools` (8 / 16 tools, frozen, with permission) ⚠️ retracted | 4.1 / 8.3 µs | 0.003 / 0.03 µs | 120 B / 184 B |
| `ToolRegistry.ResolveTools` (4 tools, unfrozen) | 0.23 µs | 0.001 µs | 600 B |
| `ToolRegistry.GetTool` (frozen) | 0.10–0.20 µs | 0.001–0.005 µs | 80–160 B |
| `ToolRegistry.ResolveTools` (14 tools) | 1.10 µs | 0.08 µs | 0 B |
| `PermissionRuleset.Evaluate` (default Allow) | 0.35 µs | 0.002 µs | 0 B |
| `PermissionRuleset.Evaluate` (Deny bash rm -rf /) | 0.17 µs | 0.001 µs | 488 B |
| `PermissionRuleset.Evaluate` (custom, Allow at end-of-scan, 4 rules) | 0.025 µs | 0.001 µs | 0 B |

> ⚠️ **Retracted by #408 — the "with permission" rows were never a comparison.**
> `ToolRegistryBenchmark` fed `PermissionRuleset.Default` to `ResolveTools`, and `Default` has no rule
> for a stub tool named `tool_N`, so `Evaluate` fell through to `Ask` and the filter resolved **0 of N**
> descriptors. The row therefore measured a *result-shape change* (N → 0 items) against an empty cached
> snapshot, not the cost of permission filtering — "2.3 µs / 88 B" is the price of a 0-of-4 lookup, and
> it looks faster than the no-permission row only because the frozen snapshot returns a cached empty
> array. Do not quote it, do not diff against it.
>
> The bench now uses an explicit allow-all ruleset (one `Allow` rule per stub tool, no safety policies)
> so the compared cases resolve the same descriptors, and `[GlobalSetup]` **throws** via
> `AssertComparable` if the resolved count, name set or descriptor type ever diverge. The replacement
> rows below come from the same CI job (the class is still matched by `*Registry*`) and are
> ⏳ pending the next run:

| Benchmark | Mean | StdDev | Allocations |
|---|---:|---:|---:|
| `ToolRegistry.ResolveTools` (4 / 8 / 16 tools, frozen, **allow-all** permission) ⏳ not yet measured | — | — | — |
| `ToolRegistry.ResolveTools` (4 / 8 / 16 tools, frozen, **deny-all** — 0 tools, *not* comparable) ⏳ not yet measured | — | — | — |
| `ToolRegistry.ResolveTools` (4 / 8 / 16 tools, unfrozen, **allow-all** permission) ⏳ not yet measured | — | — | — |

The denied shape itself is pinned by `ToolRegistryResolveShapeTests` (`tests/Harbor.Registries.Tests`,
platform shard): deny-all resolves 0 tools on both the frozen and the unfrozen path, and an allow-all
ruleset resolves exactly the unfiltered descriptor set.

### 5.2 Local full runs (2026-08-22, i5-8250U, .NET 10.0.10)

| Benchmark | Mean | StdDev | Allocations |
|---|---:|---:|---:|
| `EventBusBenchmark.PublishAsync_1Sub` ⚠️ retracted, see §5.4.4 | 0.35 µs | 0.04 µs | 0 B |
| `EventBusBenchmark.PublishAsync_NSub` (10 subscribers) ⚠️ retracted, see §5.4.4 | 2.80 µs | 0.20 µs | 0 B |
| `UiStore.Dispatch` (lock-free CAS) | 0.15 µs | 0.02 µs | 0 B |
| `JsonlSessionStore.AppendMessage` | 12 µs | 1.5 µs | 480 B |
| `JsonlSessionStore.GetMessages` (100 msgs) | 850 µs | 90 µs | 28 KB |
| `SystemPromptBuilder.Build` (10 tools) | 18 µs | 2 µs | 1.2 KB |
| `TokenEstimator.Estimate` (1k chars) | 0.8 µs | 0.1 µs | 0 B |
| `MessageConverter.ToLlmMessages` (10 msgs) | 2.5 µs | 0.3 µs | 1.5 KB |

### 5.3 Event-bus + IPC delivery split (#408) ⏳ not yet measured

Before #408 the bus rows were one blended number per class, so "event bus throughput" silently
depended on whether a consumer happened to be attached. Each of the three bus files now reports three
separately-named rows:

| File | Row | What the number includes | Consumer awaited? |
|---|---|---|---|
| `EventBusBenchmark.cs` (`EventBusDeliveryBenchmark`) | `EnqueueOnly` | ring append + publish counters on a bus with **zero subscribers** — says **nothing about delivery** | n/a (there is no consumer) |
| | `EnqueueAndDrainConsumer` | + fan-out to 10 handlers | yes |
| | `SteadyState` | awaited publish on a **saturated** ring + `GetScrollback` tail read | yes |
| `EventBusScrollbackBenchmark.cs` (`EventBusScrollbackDeliveryBenchmark`) | `EnqueueOnly` | ring slot overwrite on a full 1000-slot ring, zero subscribers — says **nothing about delivery** | n/a |
| | `EnqueueAndDrainConsumer` | + fan-out to 10 handlers | yes |
| | `SteadyState` | awaited publish + 1000-event tail read | yes |
| `EventBroadcasterThroughputBenchmark.cs` (`EventBroadcasterDeliveryBenchmark`) | `EnqueueOnly` | projection + MessagePack + per-client enqueue; the per-client writer tasks are **not** awaited — says **nothing about client delivery** | no |
| | `EnqueueAndDrainConsumer` | + drain every client pipe (bounded passes) | drained to completion |
| | `SteadyState` | 4 warm burst+drain rounds | drained to completion |

⏳ **No numbers yet.** These rows are code-only as of #408: no local full run and no CI-short run.
\#391 added a `*EventBusBenchmark*` step to the CI `benchmark` job, but that glob deliberately does
**not** match the `*Delivery*` classes, so the job still does not run them. Fill them in with
machine + date on the next local pass:

```bash
dotnet run -c Release --project tests/Harbor.Benchmarks -- --filter '*Delivery*'
```

The pre-#408 blended rows that remain valid are the P1 `EventBroadcaster` bottleneck row at the top
of this file. The `EventBus.PublishAsync` fan-out curve that used to live in §5.2 (1 / 10
subscribers) is **superseded** by §5.4: #391 replaced the single `[Params(0, 1, 10, 100)]` method
with per-case rows that also vary scrollback capacity, because that one method could never reach the
fast path. §5.4.4 records the old cells as retracted rather than comparable. `InMemoryEventBus` exposes no way to empty its scrollback ring, so the enqueue rows
run against a ring that saturates; slot overwrite costs the same as a fresh slot, which each class doc
records under `RetainedState:`.

### 5.4 EventBus publish path — retention × drain **[CI-short]** (PR `benchmark` job, `#391`)

The bus rows in §5.2 are **retracted**, not merely stale. They come from 2026-08-22, one day
before the ring-buffer landing (`2f9debf`, 2026-08-23) that replaced the `ImmutableArray`
scrollback copy, and they were produced by a single `[Params(0, 1, 10, 100)]` method that built
`new InMemoryEventBus(maxScrollback: 1024)` — scrollback on for every case. The zero-allocation
fast path in `InMemoryEventBus.PublishAsync`
(`src/Harbor.Registries/Events/InMemoryEventBus.cs:274`) requires
`_maxScrollback == 0 && _middlewares.Count == 0 && _subscriptions.IsEmpty`, so that method could
never reach it. The "8.1 KB @0 subscribers" figure is the pre-ring copy (1024 refs × 8 B ≈
8 KB), not a live allocation.

\#391 replaced it with one row per (retention × drain) point, each carrying the 7-field contract
`BenchmarkContractTests` requires. §5.3's `*Delivery*` rows are a different, still-unmeasured
split and are not touched by this section.

#### 5.4.1 Measured — run [36452709074](https://github.com/refusedguy/Harbor-Harness/actions/runs/36452709074), 2026-09-28

Commit `752196c` · `benchmark` job, `--filter '*EventBusBenchmark*' --job Short` ·
`ubuntu-latest` pinned with `taskset -c 1` · **AMD EPYC 7763 @ 2.45 GHz (2 physical cores)**,
Ubuntu 24.04.5, .NET SDK 10.0.401, runtime 10.0.12, BDN 0.15.8.
Artifact: `benchmarkdotnet-results` (`*-report-github.md`).

> Machine class note: this runner is an EPYC **7763**, not the 9V74 quoted for the
> 2026-09-09 CI-short rows. Do not compare mean times across the two — the
> allocation column is exact and runner-independent, the time column is not.

Reproducibility: a second CI run of the same branch
([36456334807](https://github.com/refusedguy/Harbor-Harness/actions/runs/36456334807),
independent runner, no code change between them) returns the **allocation column byte-for-byte
identical** — `0 / 80 / 200 / 200 / 760 / 920 / 8120 B` — and the fast path again at
8.27 ns. Means move within noise (`1Sub` 242.7 → 244.2 ns; `0Sub_ScrollbackOn` 107.5 →
106.2 ns). Treat the allocation column as the measurement and the mean as an order of magnitude.

`ShortRun` rows (the `--job Short` job; the paired `Job-NTRUNJ` pass agrees):

| Case | retention | drain | Mean | StdDev | Ratio | Allocated |
|---|---|---|--:|--:|--:|--:|
| `PublishAsync_0Sub_ScrollbackOff` | off | none | **8.15 ns** | 0.01 ns | 1.00 | **0 B** |
| `PublishAsync_0Sub_ScrollbackOn` | on | none | 107.5 ns | 0.5 ns | 13.19 | 80 B |
| `PublishAsync_1Sub` | on | 1 sync | 242.7 ns | 0.4 ns | 29.78 | 200 B |
| `PublishAsync_1Sub_ScrollbackOff` | off | 1 sync | 226.2 ns | 1.1 ns | 27.76 | 200 B |
| `PublishAsync_1Sub_AsyncDrain` | on | 1 async | 8.06 µs | 0.5 µs | 989.43 | 760 B |
| `PublishAsync_NSub` (10) | on | 10 sync | 803.5 ns | 3.2 ns | — | 920 B |
| `PublishAsync_NSub_ScrollbackOff` (10) | off | 10 sync | 824.1 ns | 58.4 ns | — | 920 B |
| `PublishAsync_NSub` (100) | on | 100 sync | 6.36 µs | 12.8 ns | — | 8120 B |
| `PublishAsync_NSub_ScrollbackOff` (100) | off | 100 sync | 25.5 µs | 69.8 ns | — | 8120 B |

#### 5.4.2 The fast path is real: 0 B/op at 8.1 ns

`PublishAsync_0Sub_ScrollbackOff` allocates **nothing** — BDN prints `-` for both `Gen0` and
`Allocated` because not a single Gen0 collection happened during the iteration, so the per-op
allocation is below the measurement threshold rather than merely small. At 8.15 ns/op a single
byte per publish would have produced hundreds of megabytes of Gen0 traffic. The exact `0 B` claim
is asserted merge-gated, not inferred from absence, by `PublishAsync_ZeroSubscribers_IsAllocationFree`
(`GC.GetAllocatedBytesForCurrentThread() == 0`), and *reachability* of that path is asserted by
`EventBusFastPathTests` (#391) — the benchmark alone cannot tell a fast path from a slow one
that happens to be cheap.

This also corrects the `AllocAttribution` of the fan-out contract: it claimed "fan-out to
`ValueTask.CompletedTask` handlers allocates nothing". It does — 80 B per subscriber
(§5.4.3).

#### 5.4.3 Residual allocations, attributed

The pre-ring `8.1 KB @0 subscribers` row is **resolved**: 8 KB was the `ImmutableArray`
scrollback copy, replaced by the fixed ring (`AppendScrollback`, `InMemoryEventBus.cs:413`). The
same bus shape now measures **107.5 ns / 80 B** — 75× faster and 100× less allocated.

The remaining bytes are fully accounted for, and the arithmetic is exact (`10 → 100`
subscribers adds exactly 80 B/subscriber; `920 = 80 + 120`; `8120 = 80 + 120 + 100 × 80`):

| B/op | Type | Site | Present when |
|---:|---|---|---|
| 80 | `Task<ValueTuple<bool, AgentEvent>>` | `RunMiddlewareAsync` — declared `InMemoryEventBus.cs:661`, result set at `:693` | any publish off the fast path (`Task.FromResult` does not cache non-primitive result types) |
| 80 | `Task<ValueTuple<DispatchOutcome, Task?>>` | `DispatchToOneAsync` — declared `InMemoryEventBus.cs:850`, result set at `:860`/`:874`/`:901`/`:923` | **per subscriber** |
| 40 | `Task` from the fan-out method | `DispatchToSubscribersAsync` — declared `InMemoryEventBus.cs:708`, awaited at `:342` | once per publish that actually fans out (identical at 1, 10 and 100 subscribers; absent from the 0-subscriber rows, which return before the fan-out) |

**The 40 B row is a `Task`, not a `CancellationTokenSource` — and the CTS row this section used to
carry was a false claim (#513).** The previous revision attributed the 40 B to the #249 pooled
budget CTS and stated that `CancellationTokenSource.TryReset()` "refuses" the instance because a
budget timer had been armed, so `ReturnBudgetCts` disposed it and every fan-out publish allocated
a fresh source. Both halves were wrong, and because this file is the declared single source of
truth for every measured number, the wrong attribution propagated: it sent a reader hunting for a
defect that did not exist.

On `release/10.0`, `TryReset()` **disarms a still-pending timer** and only then asks whether it
ever fired. The source is reusable while it is not cancelled and

```csharp
_timer is null || (_timer is TimerQueueTimer t && t.Change(Infinite, Infinite) && !t._everQueued)
```

(`CancellationTokenSource.cs:481-507`, dotnet/runtime `release/10.0`). The `_everQueued` flag is set
in `TimerQueue.FireNextTimers` (`Timer.cs:217` in the same release) — i.e. only when the timer
actually **reached the fire loop**, not when it was scheduled. A 250 ms budget that goes back to the
pool microseconds after the fan-out is done never fires, so `TryReset()` returned `true` and the
\#249 pool was recycling before #507 touched it. "Armed ⇒ not reusable" is the wrong model; if you
see that shape of argument anywhere in this repo, this paragraph is the counter-example.

The proof costs nothing to re-check, because the allocation column did not move when #507 made
recycling a structural guarantee: `920 B @10` / `8120 B @100` in #391's run
([36452709074](https://github.com/refusedguy/Harbor-Harness/actions/runs/36452709074), commit
`752196c`) and again in #507's run
([36469490918](https://github.com/refusedguy/Harbor-Harness/actions/runs/36469490918), commit
`7f9b5c6`) — byte for byte, same rows, same `AMD EPYC 7763 @ 2.45 GHz` / `taskset -c 1` runner
class. Had the 40 B been a CTS, the fix would have deleted it. It stayed, so it was never a CTS.

**What #507 bought: a guarantee, not bytes.** `DispatchToOneAsync` now arms the budget timer only
for a dispatch that outlives its synchronous part (`InMemoryEventBus.cs:865-889`), so a completed
fan-out returns a pristine source and the next publish rents the same instance *by construction*
instead of relying on the thin runtime behaviour cited above. Two regression tests hold that line —
`Publish_AcrossManyPublishes_HandsOutOneRecycledBudgetSource` and
`Publish_AcrossManyPublishes_AllocatesNoFreshBudgetSourcePerPublish`
(`tests/Harbor.Core.Tests/EventBusBudgetCtsTests.cs`), which count distinct `CancellationToken`s
over 200 publishes and therefore count `CancellationTokenSource`s, since token equality is source
identity. The **time** column did move on that run (`NSub` @10 803.5 → 463.8 ns, @100 6.36 µs →
2.88 µs — no timer arm + disarm per subscriber); the **allocation** column did not, and the
arithmetic above is unchanged (`920 = 80 + 40 + 10×80`, `8120 = 80 + 40 + 100×80`).

One honesty note, so this row is not promoted the same way twice: the 40 B is a **subtraction
result** (measured column minus the two `Task` rows above), not a per-type allocation profile. The
constant is solid; treat `DispatchToSubscribersAsync` as the current best attribution and
re-derive it from a profile before building a *mechanism* on top of it. The row that was wrong here
(#513) was wrong exactly because a subtraction result was written down as a proven mechanism.

Two rows carry zero bus signal and are annotated so nobody reads them as regressions:

- **`PublishAsync_1Sub_AsyncDrain` (8.06 µs / 760 B)** — the suspending handler is the
  benchmark's own `await Task.Yield()`, so ~7.9 µs of thread-pool post latency and most of
  the extra 560 B (`ValueTask.AsTask()` at `:726` plus the continuation/`ExecutionContext`
  machinery of a real suspension) are handler cost, not bus cost.
- **`PublishAsync_NSub_ScrollbackOff` @100 (25.5 µs vs 6.36 µs for the ring-on twin)** —
  the two rows are *identical work* minus the ring append, so the 4× spread is shared-runner noise
  on a 2-physical-core runner, not a property of scrollback. The allocation column (8120 B,
  identical in both jobs and in both rows) is the trustworthy part.

Instability watch, in the spirit of the §5.1 note: `NSub` @100 means ranged 6.4–25.5 µs across
the two job passes (`Error` up to 22 µs). Re-measure on a quiet runner before quoting a
100-subscriber mean.

#### 5.4.4 What this replaces

| Old row (2026-08-22, i5-8250U) | Was | Now (2026-09-28, EPYC 7763) |
|---|---|---|
| `EventBus.PublishAsync` (0 sub) — 8.1 µs / 8.1 KB | pre-ring, scrollback always on | 107.5 ns / 80 B (`PublishAsync_0Sub_ScrollbackOn`) — and **8.15 ns / 0 B** with scrollback off |
| `EventBus.PublishAsync` (1 subscriber) — 0.35 µs / 0 B | ⚠️ retracted: a fan-out to a completed handler *does* allocate | 242.7 ns / 200 B |
| `EventBus.PublishAsync` (10 subscribers) — 2.80 µs / 0 B | ⚠️ retracted, same reason | 803.5 ns / 920 B |

The old `0 B` cells were wrong before this slice: they predate #47/#152/#249, and a benchmark
that always ran with scrollback on could not have seen a per-subscriber result `Task`. They are
kept above only as a visual diff and are not comparable row-for-row (also different machine class).

> **Superseded guard, still-valid numbers (#47/S3).** The rows above were measured on the pre-S3
> guard (`_middlewares.Count == 0 && _maxScrollback == 0 && _subscriptions.IsEmpty`, cited at
> `InMemoryEventBus.cs:224` in the S1-era source). S3 replaced the first term with "no sink declared
> `EventBusSinkKind.Mandatory`" and moved the guard out of the `async` method, so
> `PublishAsync_0Sub_ScrollbackOff`'s 8.15 ns becomes **1.46 ns** for the same composition (§5.5).
> The 0 B/op verdict and the discriminator this section relies on (`PublishedCount` stays 0) are
> unchanged: S1's reachability tests still hold as written.

### 5.5 Zero-subscriber fast path (#47/S3) ✅ measured in CI

`EventBusFastPathBenchmark` measures acceptance cost for four compositions of the same publish, so the
fast path is a number rather than a claim, and the near-misses that disqualify a bus are visible next
to it. Run by the CI `benchmark` job (`--filter '*EventBusFastPath*'`, Short job); a local full run:

```bash
dotnet run -c Release --project tests/Harbor.Benchmarks -- --filter '*EventBusFastPath*'
```

These rows measure ACCEPTANCE cost, not delivery — the qualifying rows deliver to nobody.

| Row | Composition | Expected allocation |
|---|---|---|
| `Qualifying_0Sub_NoSinks_ScrollbackOff` | 0 subscribers, 0 scrollback, no sinks | **0 B/op** |
| `Qualifying_0Sub_OptionalSinkDrained_ScrollbackOff` | + one optional sink (sampler), drained inline | **0 B/op** |
| `Disqualified_0Sub_MandatorySink_ScrollbackOff` | + one mandatory sink (type filter) | allocates by design |
| `Disqualified_1Sub_NoSinks_ScrollbackOff` | one live subscriber | allocates by design |

Measured 2026-09-28 on the CI runner (ubuntu-24.04, `taskset -c 1`), BDN
`[SimpleJob(warmupCount: 3, iterationCount: 5)]` + `[MemoryDiagnoser]`, commit `f8c9e95`:

| Row | Mean | Allocated |
|---|---|---|
| `Qualifying_0Sub_NoSinks_ScrollbackOff` | **1.46 ns** | **0 B** |
| `Qualifying_0Sub_OptionalSinkDrained_ScrollbackOff` | 14.5 ns | **0 B** |
| `Disqualified_0Sub_MandatorySink_ScrollbackOff` | 83.5 ns | 112 B |
| `Disqualified_1Sub_NoSinks_ScrollbackOff` | 159.2 ns | 200 B |

Reading the four rows: the qualifying publish is **~1.5 ns and allocation-free** (a composition-time
field read, one lock-free subscription-snapshot read, one `Interlocked` for the fast-path counter,
`Task.CompletedTask` — no async state machine since #47/S3). Draining an optional sink costs ~13 ns
more (a second `Interlocked` plus a virtual call through the sink interface) and still allocates
nothing: that is the price of "optional" without "silent". The two disqualified rows are what a bus
pays when something *can* observe the publish.

`Qualifying_0Sub_NoSinks_ScrollbackOff` is the same composition as §5.4's
`PublishAsync_0Sub_ScrollbackOff`, measured on the same runner class: **8.15 ns → 1.46 ns** is the
async-state-machine removal, and both report 0 B/op.

Caveat: the same run's `ShortRun` pass reported 96.9 ns ± 692 ns for the optional-drain row (3
iterations, one extreme outlier) against 14.5 ns in the table. Treat that row's latency as indicative;
its **allocation** (0 B) is the load-bearing claim, and it is also asserted — not eyeballed — by
`EventBusSinkVerdictTests` in `tests/Harbor.Core.Tests` with
`GC.GetAllocatedBytesForCurrentThread() == 0` over 5 000 publishes.

Which production compositions actually qualify is measured per preset in
`tests/Harbor.Hosting.Tests/EventBusSinkCompositionTests.cs` and tabulated in
[`docs/EVENT_BUS_SINKS.md`](./EVENT_BUS_SINKS.md) §5.

> **Corrected by #518 — the "0 % for every shipped preset" attribution above was wrong, and so
> was the sentence explaining it.** Until #518 the fast-path guard read
> `maxScrollback == 0 && no mandatory sink`, so the shipped 1000-slot scrollback capacity
> disqualified the fast path by itself. The claim that "the mandatory/optional verdict, not the
> guard, is what keeps them out" held only for the **CLI** preset, whose `TypeFilterMiddleware` is
> genuinely mandatory; the **desktop** preset has no sinks at all and was being kept out by the
> scrollback term alone — a ring that was written on every publish and read by no one.
>
> Retention is now armed by the first `GetScrollback` call, so the guard reads "no mandatory sink
> and an unarmed ring". Composition-level result, asserted by
> `EventBusSinkCompositionTests` (qualifying publishes out of 200, zero subscribers):
>
> | Preset | Before #518 | After #518 | Reason |
> |---|---:|---:|---|
> | Desktop (`DesktopDefault`, no sinks) | 0/200 | **200/200** | ring was never read → never maintained → publish is unobservable |
> | CLI (`CliOptions` + `TypeFilterMiddleware`) | 0/200 | 0/200 | the type filter is mandatory and still keeps the full path |
> | Headless (`EventBusScrollback = 0`) | 200/200 | 200/200 | unchanged — no ring to maintain |
>
> **The latency figures in §5.4 and §5.5 above are not re-measured for #518** and still describe
> the pre-#518 code path; the tables are left as the record of what was measured, not updated with
> numbers nobody has taken. The *qualification* rows are exact — they are asserted in CI by
> `EventBusFastPathTests` / `EventBusSinkCompositionTests`, not eyeballed. Re-run
> `dotnet run -c Release --project tests/Harbor.Benchmarks -- --filter '*EventBus*'` to refresh the
> timings; the `*_ScrollbackOn` rows in `EventBusBenchmark` now arm retention in `Setup`, without
> which they would have measured the fast path under a slow-path label.

### 5.6 Status-bar packing (`StatusBarLayoutFitBenchmark`, #487) ⏳ not yet measured

`Fit` is the status bar's packing pass: it runs on **every painted status frame**, i.e. while
tokens stream, so its cost is part of the frame budget and not a startup cost. #487 changed its
shape from "re-sum the whole row inside the shrink loop" to "measure every segment once, carry
the running total", and moved the per-run width cache it reads from a process-global table
behind a lock to a per-thread one.

```bash
dotnet run -c Release --project tests/Harbor.Benchmarks -- --filter '*StatusBarLayoutFit*'
```

> **Numbers pending.** The row table below is filled from the first BDN run of
> `StatusBarLayout_Fit_TwelveSegments` on the CI `benchmark` job; until then the
> machine-independent gate is the one that counts, and it is already enforced —
> see the tripwire table below. Do not quote a figure here that is not in a
> BDN run.

The measurement contract is in the class doc (`Operation` / `Payload` / `StateReset` / `Drain` /
`RetainedState` / `AwaitSemantics` / `AllocAttribution`, per #408). Two properties of the payload
are worth knowing before reading any number off it:

- the row is **model + mode hint (fixed) + N flexible segments**, packed to a width that leaves the
  fixed pair plus three flexible ones — the shape where the shrink loop actually iterates. A row
  that already fits measures almost nothing and would flatter the old code;
- the row is **restored from a pristine template inside the measured region**, because `Fit`
  mutates the span in place. That copy is a `Span`-typed `Array.CopyTo` into a preallocated
  buffer: 0 B, and identical per segment in every row, so the segment-count slope is not an
  artefact of the harness.

`StatusBarLayout_Fit_TwelveSegments` is the row the CellForge footer actually composes
(`ChatScreenLayout` sizes its compose buffer at 12); the `Segments` sweep carries the pathological
24-segment row that exposed the quadratic.

**Why a stopwatch is not the gate.** The load-bearing claim — a `Fit` call measures each segment
exactly once, whatever the outcome — is a property of the algorithm, so
`StatusBarFitPerfTests.Fit_MeasuresEachSegmentOnce_*` asserts it through
`UnicodeWidth.BeginWidthLookupTracking`, which counts `WidthCached` calls on the calling thread
only. Before the fix a 24-segment row packed down to its 2-segment fixed pair issued 323 of them;
it now issues 24, and the assertion holds on any machine. This is the #465 lesson applied: a
wall-clock assertion only fails on a machine slow enough to notice.

### 5.7 Run-budget check (`RunBudgetBenchmark`, #404) ⏳ not yet measured

`CheckCap` is the per-turn/per-delta budget gate: three threshold compares (tokens / tariff /
output bytes) behind one null-branch when the agent sets no caps. Run by the CI `benchmark`
job pattern (`--filter '*RunBudget*'`); locally:

```bash
dotnet run -c Release --project tests/Harbor.Benchmarks -- --filter '*RunBudget*'
```

| Benchmark | Mean | Allocated |
|---|---|---|
| `CheckCap (caps set, no trip)` ⏳ not yet measured | — | — |
| `RecordOutputBytes + CheckCap (streaming delta path)` ⏳ not yet measured | — | — |

> **Numbers pending.** The rows above are filled from the first run of `RunBudgetBenchmark`
> on the CI `benchmark` job (which runs on pushes to `dev`); until then no figure here may be
> quoted. The load-bearing claim is structural, not measured: with no caps the loop pays a
> single null-branch per turn, and with caps the check is integer/decimal compares — never a
> dictionary lookup per message. That justification is argued in the #404 PR description.

### 5.8 Object pools — builder v2 + `DictionaryPoolBenchmark` (#1239) ⏳ not yet measured

The hunt verdict on #1239 was **TAKE only via bench**: three renters
(`SystemPromptBuilder:110`, `CompactionService:359/475`, `ProcessGitQuery:205`) share one global
`ConcurrentBag<StringBuilder>`, so whichever caller left a 16 KiB builder in it decided the
capacity of every later rent — the `PLAN.md` memory-efficiency bug. `src/Harbor.Extensions/PLAN.md`
demanded proof the pool beats `new StringBuilder()` on a ~10 KiB prompt *before* shipping a
bucketed rewrite, and demanded a `DictionaryPool` counterpart.

What landed in #1239:

- **`StringBuilderPool` v2** — the bag became an array of bags indexed by `ceil(log2(capacity))`
  (16 … 16384 chars, 11 buckets, ≤16 builders per bucket). A rent is served from the smallest
  bucket that satisfies it, a returned builder is filed under the capacity it actually grew to,
  and anything past 16 KiB is still dropped on return.
- **`DictionaryPool<TKey, TValue>`** — the P1 counterpart, same shape over entry bands
  (8 … 1024 entries). Comparers are deliberately not pooled: an instance is only recycled into a
  renter that wants `EqualityComparer<TKey>.Default`, and a return with any other comparer (or
  more than 1024 entries) is dropped.
- **Both are hand-written pools, not `DefaultObjectPoolProvider`.** The issue named the
  Microsoft type as one option; `src/Harbor.Extensions` is one of the four projects
  `tools/check-abstractions-contract.py` guards, and a `Microsoft.Extensions.ObjectPool`
  `<PackageReference>` there is a dependency every consumer inherits. The hand-written pool is
  AOT-safe, needs no package, and is the direction `PLAN.md` already specified.

Rows (measurement contract in each class doc, per #408):

| Benchmark | Class | What it answers |
|---|---|---|
| `Pooled 10KB prompt assembly` vs `new StringBuilder 10KB prompt assembly` | `StringBuilderPoolBenchmark` (#1239 rows) | does the pool beat `new` on the ~10 KiB system-prompt shape — the question `PLAN.md` asked |
| `Pooled Rent/Append/Return 1KB` / `16KB` vs the raw baselines | `StringBuilderPoolBenchmark` | the existing rent/return cycle at both ends of the retain range |
| `Pooled 8-entry dictionary` / `256-entry` vs `new Dictionary` | `DictionaryPoolBenchmark` (new in #1239) | does pooling pay for a cheap-to-construct dictionary, or only for rent/return |
| `Pooled rent/return only (no entries)` | `DictionaryPoolBenchmark` | the pool's own cost, isolated — the floor the reuse rows have to clear |

```bash
dotnet run -c Release --project tests/Harbor.Benchmarks -- --filter '*Pool*' --buildTimeout 600
```

| Benchmark | Mean | Allocated |
|---|---|---|
| `Pooled 10KB prompt assembly` ⏳ not yet measured | — | — |
| `new StringBuilder 10KB prompt assembly` ⏳ not yet measured | — | — |
| `Pooled 8-entry dictionary` ⏳ not yet measured | — | — |
| `new Dictionary 8 entries` ⏳ not yet measured | — | — |
| `Pooled rent/return only` ⏳ not yet measured | — | — |

> **Numbers pending — why there are none here, stated rather than implied.** The `benchmark`
> workflow runs on pushes to `dev`/`master` only (not on PR branches), behind a cooldown, and its
> curated `--filter` set does not match these classes; the `perf-gate` job in `ci.yml` is the TUI
> renderer gate, not a BenchmarkDotNet run. Nothing in this change's path measured a wall-clock
> number, and a figure typed here without a run is exactly the unattributed number the #408
> contract exists to forbid. Fill the table from the first local full run or from a
> `workflow_dispatch` of `benchmark`, and record machine + date next to it.

**What is measured in the meantime is machine-independent.** The load-bearing claims of #1239 are
structural, so they are pinned as invariants in `tests/Harbor.Abstractions.Tests` rather than as
timings — and unlike a wall-clock assertion, they fail on any machine:

| Invariant | Test |
|---|---|
| a rent of N chars is served by a builder of at least N chars | `StringBuilderPoolTests.Rent_ReturnsBuilderOfAtLeastRequestedCapacity` |
| a 256-char rent never inherits the neighbour's 16 KiB builder (the bug this fixes) | `StringBuilderPoolTests.SmallRent_AfterLargeReturn_StaysInItsOwnBucket` |
| rents from the same band recycle the same instance (pooling actually pools) | `StringBuilderPoolTests.RepeatedRents_FromSameBucket_ReuseTheSameBuilder`, `DictionaryPoolTests.RepeatedRents_FromSameBand_ReuseTheSameDictionary` |
| a rent above the retain cap is unpooled and not recycled | `StringBuilderPoolTests.RequestAboveRetainCap_AllocatesUnpooledBuilder`, `DictionaryPoolTests.DictionaryThatOutgrewTheLargestBand_IsDropped` |
| a custom comparer never leaks into a default-comparer rent | `DictionaryPoolTests.CustomComparer_IsNeverRecycledIntoADefaultRent` |
| concurrent renters never share one instance | both classes' `ConcurrentRents_*` |

> **Not yet done (deliberately, and outside this slice):** no product call site consumes
> `DictionaryPool` yet. Wiring it into the per-call argument dictionaries is a separate change in
> the hot-path area, and it should land with its own allocation tripwire rather than ride along
> with the pool that makes it possible.

### Per-stage counters — what the instrumentation costs (#409, #46 slice 2)

**There is no per-stage *time* breakdown of the TextDelta → visible-frame pipeline in this file,
and the reason is structural, not an omission.** The four stage names are not four sequential
steps. On the shipped CellForge path the first three are one nested call chain:

```
frame → TimelineLayoutCache.PrepareLayout → SettleVisible → IChatBlock.Measure
                                                            ↓  (Measure calls this FIRST)
                                                        EnsureRendered / RenderTail
                                                            ↓  (only past the memo)
                                                        MarkdownBlockParser.ParseInto
frame → DiffEngine.Flush → AnsiWriter → backend.WriteAsync      ← the only separate stage
```

A block cannot report its own height without rendering itself, so **layout drives materialize,
and materialize contains parse**. Only `write` is genuinely downstream — a different assembly
(`Harbor.Tui.CellForge.Engine`), after paint, and the only stage that reaches a device. The
consequence for anyone reading counters: the invariant is
`TerminalWrites ≥ Materializations ≥ MarkdownParses`, **the four do not sum to anything**, and no
gate may assert on their total. What they buy is frequency, which is what #410 gates on and
what #412 needs in order to prove virtualization is real.

So #409 counts **call frequencies at four existing boundaries** — no split of the fused chain, no
hot-path refactor, no behaviour change. `UiStageCounters`
(`Harbor.Ui.Framework.Rendering/PerformanceContracts/`) holds one `long` per stage behind a single
`Enabled` switch, incremented with `Interlocked`.

**Cost of the instrument, measured.** A single-file probe (20 M iterations, Release, the same
guard/counter shape) timed three arms — guard off (branch only), guard on with one
`Interlocked.Increment`, and six increments:

| Arm | Cost | Hits counted |
|---|--:|--:|
| guard off — one static bool read + branch | 3.92 ns | 0 |
| guard on — +1 `Interlocked.Increment` | 11.48 ns (**+7.56 ns**) | exactly 1 per call |
| guard on — +6 `Interlocked.Increment` | 46.33 ns (**+42.41 ns**) | exactly 6 per call |

Against the rows above, which are the source of truth for the stages being instrumented:

| Placement | Ratio |
|---|--:|
| 1 counter **per frame** vs the 51.3 µs idle frame | **0.015 %** |
| all 4 counters **per frame** vs the 51.3 µs idle frame | **0.083 %** |
| all 4 counters **per frame** vs the 805 µs solve+paint+diff+encode frame | **0.005 %** |
| 1 counter **per delta** vs the 475 ns/delta store path | **1.59 %** |

**Decision, recorded because it is the one that matters.** Every counter here fires per frame or
rarer — none per delta — so the instrument lands at 0.083 % of an idle frame and is in. The
per-delta store path is deliberately left uninstrumented: at 1.59 % per counter it is the same
order as the quantity it would measure, and #984 struck rows rather than converting them for
exactly this reason — a per-frame budget is not "once per call". Six counters on the per-delta
path would have cost **8.9 %** of the 475 µs run it was meant to explain. That is the counter
placement this slice declines, not a counter it omits.

Probe machine: linux-x64, .NET 10 Release JIT. The probe is gated on non-vacuity in both
directions — guard-off counted **exactly 0** and guard-on **exactly N**, and guard-on was
strictly slower than guard-off — because an instrument that reads 0 for free is the #591 shape and
would make every ratio above fiction. `UiStageCounterTests` carries the same discipline into CI,
including a guard-off arm over an identical workload whose occurrence is proved by the backend's
recorded write and the layout cache's own tally rather than by a counter.

### Allocation-budget tripwires (#186, CI-enforced)

Steady-state allocation coverage for paths the microbenchmarks above don't
pin. Zero-alloc cells assert `GC.GetAllocatedBytesForCurrentThread() == 0`
after warmup (thread-scoped, parallel-safe); bounded cells are generous
tripwires in the `SpanParserTests` tradition — per-op averages are printed
to stdout for the next BENCHMARKS refresh, hard failures only on
pathological growth.

**How a window is measured (#741).** Every cell measures through
`Harbor.TestKit.AllocationProbe`, and which entry point it uses is forced
by the code under test, not by taste:

| Window | Entry point | Why |
|---|---|---|
| may contain an `await` | `MeasureProcessAsync` (process-wide) | a per-thread counter is not a smaller number across a hop, it is a *different* number |
| provably cannot suspend | `MeasureThread` (per-thread) | exact, and needs no serialisation |

A process-wide window additionally requires the class to be keyless
`[NotInParallel]`: the counter bills every allocation in the process for
the duration of the window, and TUnit runs classes in parallel by default.

**Every bounded cell asserts a floor as well as a ceiling.** This is not
decoration. `GetAllocatedBytesForCurrentThread()` sampled on both sides of
an `await` returns the gap between two *unrelated* thread counters, and on
the `AgentLoop` turn path that gap was **negative** — `text-only turn avg =
-815859 B`. A ceiling-only check is satisfied by every negative number, so
that gate was not imprecise, it was **off while rendering green**. A ceiling
is only a gate once something can fail underneath it.

Run per project, e.g.:

```bash
dotnet run -c Release --project tests/Harbor.Registries.Tests -- --treenode-filter "*/*/*Allocation*"
```

| Test | Path | Ceiling |
|---|---|---|
| `PublishAsync_ZeroSubscribers_IsAllocationFree` (`Harbor.Registries.Tests`) | `InMemoryEventBus` 0-sub fast path | 0 B |
| `QualifyingComposition_ReturnsCompletedTask_AndAllocatesNothing` (`Harbor.Core.Tests`, #47/S3) | 0-sub / 0-scrollback / no-sink fast path, under the mandatory-sink guard | 0 B |
| `OptionalSink_IsDrainedOnTheFastPath_NotSkipped` (`Harbor.Core.Tests`, #47/S3) | same + an optional sink drained inline | 0 B |
| `PublishAsync_ZeroSubscribersNoScrollback_TakesFastPath` (`Harbor.Registries.Tests`, #391) | fast-path *reachability* — `PublishedCount` stays 0, no scrollback/no subs | exact |
| `PublishAsync_ZeroSubscribersWithScrollback_LeavesFastPath` (#391) | scrollback on ⇒ slow path (`PublishedCount` advances) | exact |
| `PublishAsync_OneSubscriberNoScrollback_LeavesFastPath` (#391) | one subscriber ⇒ slow path | exact |
| `PublishAsync_SingleSubscriber_StaysBounded` | `InMemoryEventBus` 1-sub fan-out | ≤ 1 KB/publish |
| `PublishAsync_TenSubscribers_StaysBounded` | `InMemoryEventBus` 10-sub fan-out | ≤ 2 KB/publish |
| `ResolveTools_FrozenUnfiltered_IsAllocationFree` | frozen `ToolRegistry.ResolveTools` (no permission, cached array) | 0 B |
| `ResolveTools_FrozenWithPermission_IsAllocationFree` | frozen `ToolRegistry.ResolveTools` (same ruleset, memoized) | 0 B |
| `TextOnly_Turn_StaysBounded` (`Harbor.Application.Tests`) | `AgentLoop` text-only turn | ≤ 512 KB/turn, **and ≥ 0** (#741) |
| `ToolCall_Turn_StaysBounded` | `AgentLoop` tool turn incl. `StreamingCoalescer` materialize/`TryParseArgs` | ≤ 2560 KB/turn, **and ≥ 0** (#741) |
| `Parse_UserLine_StaysBounded` (`Harbor.Storage.Jsonl.Tests`) | `JsonlLineParser.Parse` per line | ≤ 8 KB/line |
| `GetMessages_SeededStore_StaysBounded` | `JsonlSessionStore.GetMessagesAsync` (100 msgs) | ≤ 512 KB/read |
| `TryParseChatChunkLine_TextDelta_StaysBounded` (`Harbor.Providers.Tests`) | `OpenAiWire.TryParseChatChunkLine` text chunk | ≤ 4 KB/chunk |
| `DecodeDataLine_KilobytePayload_AllocatesNothing` (`Harbor.Providers.Tests`, #467) | `SsePump.DecodeDataLine` — SSE `data:` prefix strip + `[DONE]` test on a 4 KB line (above the chunk parser, so outside the row above) | 0 B |
| `LegacyDecode_StillCopiesPayload_TripwireIsNotVacuous` (#467) | the pre-#467 chain kept verbatim (`Substring` → `TrimStart` → `Trim().Equals("[DONE]")`) on the same 4 KB line — keeps the zero-alloc gate from passing vacuously | > 2 bytes/char |
| `Style_SweepsEveryStyleCombination_AllocatesNothing` (`Harbor.Tui.RendererTests`, #493) | `AnsiEscapeStrategy.Style` — a table read over all 64 `TuiStyle` combinations, once per styled run (not per frame) | 0 B |
| `PerStyledRunStringBuilder_StillAllocated_TripwireIsNotVacuous` (#493) | the pre-#493 chain kept verbatim (throwaway `StringBuilder(11)` + `ToString()` per styled run) over the same 64 combinations | > 64 B |
| `ToolCallDelta_AppendsEveryFragment_WithoutWritingToTheTable` (`Harbor.Application.Tests`, #493) | `StreamingCoalescer.AppendToolCallDelta` — not an allocation cell: the removed write-back was a hash + bucket store (0 B, non-zero cycles), so it is gated on the table's own modification contract instead | exact |
| `PerDeltaProjection_CollapsedAgainstLegacyChain` (`Harbor.Tui.CellForge.Tests`, #466) | `CellForgeTuiRenderer` store→widget projection per `TextDeltaEvent` (200 sessions, 128-char draft, sidebar attached) — the frame tick plus the slice guards + `SideBarProjectionCache`, minus the pre-#466 chain measured on the same store | ≤ (legacy − 1600 B) |
| `LegacyProjection_StillCopiesSessionsPerDelta` (#466) | the pre-#466 projection body kept verbatim (11 setters + `PromptBuffer.SnapshotText()` + `SideBarView.ProjectFromStore` per notification) on the same store — keeps the collapse gate from passing vacuously | > 1600 B/delta |
| ~~`ExtractDiff_NonDiffTool_IsAllocationFree`~~ / ~~`ExtractDiff_Edit_StaysBounded`~~ (#570) | **removed with the copy they measured.** Both drove `Harbor.Tui.CellForge.Rendering.DiffPreview.ExtractDiff`, a third copy of the diff-preview walk with no product caller — the tool card reads its block from `Ui.Framework.State.DiffPreview` and CellForge never computed one. A figure for a deleted method is not a measurement of anything, so the rows go rather than being re-pointed at the State copy: that copy is called ONCE per tool call by `ChatAppReducer`, not per frame, so these per-frame budgets never described it | — |
| `Fit_AllocatesNothing_WhenTheRowIsResolvedByDroppingSegments` (`Harbor.Ui.Framework.Tests`, #487) | `StatusBarLayout.Fit` drop path (8 segments → 5) | 0 B |
| `StatusAndSpinner_SteadyState_AllocationFree` (`Harbor.Tui.CellForge.Tests`) | `BuildSegments` + `Fit` + `StatusBarWidget.Paint` + spinner, 79 cells | 0 B |

### #487 tripwires — status-bar packing (`Harbor.Ui.Framework.Tests.StatusBarFitPerfTests`)

| Test | Claim | Gate |
|---|---|---|
| `Fit_MeasuresEachSegmentOnce_NoMatterHowManyAreDropped` | width lookups per call, 24 segments → 2 kept | exactly 24 (pre-#487: 323, each a monitor acquisition) |
| `Fit_MeasuresEachSegmentOnce_AtEveryWidth` | same, across 12 widths incl. the character-cut path | exactly 12 per width |
| `Fit_Cost_GrowsLinearly_WithSegmentCount` | 4× the segments, same survivor count — min-of-3, linux-gated | ratio < 8 (linear ~4; re-summing 17 → 314 lookups, 18.5×) |
| `WidthCache_LookupsFromFourThreads_AreNotSerialised` | 4 threads × a full cold pass, min-of-3, self-calibrating skip | ratio < 2.0 (a shared monitor: ~4.0) |
| `Fit_AllocatesNothing_WhenTheRowIsResolvedByDroppingSegments` | drop-only packing is allocation-free | 0 B |

The fourth row is the lock half of #487. It calibrates first: if the runner cannot get 2× out of
four threads on pure CPU work, the test prints `SKIPPED` instead of failing, because that outcome
says nothing about the code.

### Store-path scaling gates (#410) — **PER-UNIT BUDGET** + **COUNTS**, CI merge gate

**Converted in #1948** — the cost gate here is now a BUDGET PER UNIT OF WORK (bytes per unit, hard; probe units per unit, soft) over a FIXED amount of work. The rows below are the pre-conversion measurement, kept as the dated record; the current budgets are in [the #1948 section](#the-ratio-form-gates-become-per-unit-budgets-1948--per-unit-budget-ci-merge-gate).


`Harbor.Ui.Framework.Tests.StorePathScalingGates`, in the `test (ui)` merge-gate shard.

**These rows are relative claims, not measurements.** Every gate compares a
quantity against the SAME quantity measured over twice the input *inside one
run*, so the runner's speed appears in both operands and cancels. The absolute
milliseconds a run happened to produce are printed to the job summary (#618)
for information and are **not** what any gate reads — do not read the absolute
columns as a target, and do not "fix" a red gate by editing a number here.
This is the opposite convention to every other table in this file, and it is
deliberate. The #60 finding these gates enforce (`UiStore dispatch +
DefaultUiProjector` per delta: 1000 → 475 µs / 1.03 MB, 2000 → 1.09 ms /
2.46 MB) was never portable to begin with. Across six green `test (platform)`
runs of `dev` on one unchanged commit,
`DebouncedPluginWatcherTests.QuickSaveBurst_CollapsesToSingleModified`
measured 2.167 / 2.624 / 2.938 / 3.009 / 3.088 / 2.702 s — a **1.43× spread** —
and the run that reddened (#980, run 36851514483) read 178 ms for five 8-byte
file writes against a 125 ms budget. A millisecond threshold on shared CI
hardware is a wrong METRIC, not a badly chosen number: no value fixes it,
because a tighter one flakes more and a looser one stops testing what it was
written for.

| Test | Relative claim | Gate | Measured (run 36860503050) |
|---|---|---|---|
| `Cost_GrowsSubLinearly_InDeltas` | time over the store path, 1000 → 2000 deltas — warm-up discarded, best-of-3, linux-gated | ratio ≤ 3.0 (linear ≈ 2.0; O(N²) ≈ 4.0) | **1.99** (0.996 → 1.985 ms) |
| `Cost_GrowsSubLinearly_InDeltas` | the same claim for allocations, which do not move with runner speed at all | ratio ≤ 3.0 | **1.99** (1157.8 → 2298.7 KiB) |
| `Shape_IsBoundedBy_Folds_And_Flushes_NotBy_Deltas` | transcript recomposed per FOLD, never per delta | `FullProjections ≤ folds + 2` | 2 ≤ 4 (2 folds) |
| `Shape_IsBoundedBy_Folds_And_Flushes_NotBy_Deltas` | the projector rebuilds the tail at most once per reducer flush **plus once per `IsStreaming` flip** (`IsStreaming` is part of the tail's identity in `ProjectTail`). Counted from the **projector's own signal** — a new transcript model — so a projector re-resolving the tail on unflushed deltas moves this counter and not the reducer's | `TailRebuilds ≤ PolicyFlushes + (2·Messages − 1)`, against `StreamingSync.ShouldFlush` replayed over the same chunks | 67 ≤ 69 (66 policy flushes) |
| `Shape_IsBoundedBy_Folds_And_Flushes_NotBy_Deltas` | the reducer's string-copy work is sublinear in deltas — the O(N²) `+` that `ChunkedBuffer` exists to prevent | `Materializations ≤ deltas/10` | 64 ≤ 100 (fast-path share 0.932) |
| `Shape_IsBoundedBy_Folds_And_Flushes_NotBy_Deltas` | `MarkdownParses == 0` on the store path, measured through a proxy: already-projected transcript lines returning as a **different instance**. The store path styles whole lines with no parser behind it (`DefaultUiProjector.ResolveSpans`), and any parse — markdown included — necessarily allocates a new line instance | exactly 0 | 0 |
| `TheRatioRuleAnswersTheDeclaredQuestion` | the ratio rule itself, over a fixed table of synthetic shapes with the limit carried **per row**, so retuning the constant cannot silently rewrite the control. Five of the seven rows must be **rejected**, including a zero and a negative baseline | 0 mismatches | 0 |

Two properties make these gates rather than decorations, and both were paid for
in failures first:

- **Every counter is shown non-zero before it is bounded.** A count of zero
  satisfies every `≤`, so `Dispatches`, `Projects`, `FastPathHits`, `Folds` and
  `PolicyFlushes` are each asserted to have counted something real *first*.
  This is the #901 shape — checks passing on zero subjects.
- **The gate shipped red.** The first commit carried a deliberately tightened
  1.05× limit, which fails on a correct tree, and the follow-up relaxed it to
  3.0× with the reasoning recorded in the constant's own doc comment. Run
  36860503050 measured ratios of 1.99 against the 1.05 limit — the failure
  message is the gate working. A gate never observed red is not a gate — and it
  is also why the factor is 3 and not 2 (2 reddens a correct tree whenever one
  leg took a collection the other did not: the #939 shape) nor 8 (8 sits above
  the quadratic shape the gate exists to catch, which is what #465 rejected).
- **That same red run caught a bound that was too TIGHT rather than too
  loose.** `TailRebuilds ≤ flushes + 1` was my first cut, and the correct tree
  measured exactly 67 against a bound of 67. A gate that passes by zero margin
  fails on the next runner for a reason that has nothing to do with the code —
  the same class of defect as an absolute millisecond, one step further from
  visible. The bound is now derived rather than fitted: a tail rebuild is
  caused by every `IsStreaming` transition as well as by every flush, and the
  script produces `2 · Messages − 1` of those, so the correct tree sits at 67
  against 69.

The script is **two** streaming messages, not one, and that is a non-vacuity
requirement rather than realism: with a single message the transcript is empty
until the final fold, so no already-projected line ever exists to compare
against and the restyle counter would read 0 on any tree whatsoever. The
second fold is what gives `ProjectHistory`'s common-prefix scan something to
reuse — drop that scan and line 0 comes back as a new instance and the counter
fires.

### MatchesPattern scaling gate (#1528) — **PER-UNIT BUDGET**, CI merge gate

**Converted in #1948** — the cost gate here is now a BUDGET PER UNIT OF WORK: **0 B
per `Evaluate` call** (the exact zero, and the strongest shape a byte claim can take)
and 3 000 probe units per call (soft), over a FIXED 2 400 calls. The leg scaler's
8–17x-over-target legs (876–1 838 ms) are gone with the ratio they served: the gate
stopped paying 26.5 s of its 27.9 s to prove a number that never moved, and the
assembly's wall went from 18.542 s to 3.495 s.

`Harbor.Abstractions.Tests.MatchesPatternScalingGates`, in the `test (core)` merge-gate shard.

Same relative convention as #410: N against 2N inside one run, no absolute
budgets. The slice: `PermissionRule.MatchesPattern` allocated 2 strings per
call (`Pattern.Replace` + `argPath.Replace`) for every rule evaluated.
Canonicalisation moved to `BoundedPatternRegexCache.CompileGlob` — once per
distinct pattern, cache miss only — with `/` compiling to a `[\\/]`
separator class, so per-call matching passes the raw strings with identical
`'\\' ≡ '/'` semantics.

| Test | Relative claim | Gate | Measured (run 38855125995, PR #1600) |
|---|---|---|---|
| `Cost_GrowsLinearly_InRuleCount` | time over N-rule `Evaluate` miss-walks, 256 → 512 rules — warm-up discarded, best-of-5 | ratio ≤ 3.0 (linear ≈ 2.0; O(N²) ≈ 4.0) | **1.39** (17.058 → 23.767 ms) |
| `Cost_GrowsLinearly_InRuleCount` | allocations over the same walks — exact-zero, not a ratio: 2 wasted strings per rule are still linear, so a ratio gate passes with the bug in place | exactly 0 B on both windows | 0 → 0 B |

### Prompt-cache key scaling gate (#1532) — **PER-UNIT BUDGET**, CI merge gate

**Converted in #1948** — the cost gate here is now a BUDGET PER UNIT OF WORK (bytes per unit, hard; probe units per unit, soft) over a FIXED amount of work. The rows below are the pre-conversion measurement, kept as the dated record; the current budgets are in [the #1948 section](#the-ratio-form-gates-become-per-unit-budgets-1948--per-unit-budget-ci-merge-gate).


`Harbor.Application.Tests.CachingSystemPromptBuilderKeyScalingTests`, in the
`test (core)` merge-gate shard.

Same relative convention as #410/#1528: N against 2N inside one run, no
absolute budgets. The slice: `ComputeKey` used to join EVERY context field
into one `StringBuilder` (doubling the context bytes per turn) and hash the
whole prompt text twice more (`GetBytes` → whole-context `byte[]`, then
`SHA256.HashData`), and it kept a static
`ConcurrentDictionary<JsonDocument, string>` keyed by document REFERENCE — a
fresh doc per tool resolution could never hit, so the map never once served a
hit and grew one entry per distinct document instance, unbounded.

The rewrite streams every field's UTF-8 encoding into ONE
`IncrementalHash(SHA256)` through a ~243-byte bounded stack buffer — the
joined string and the whole-context byte array no longer exist — and hashes
tool schemas in place as canonical JSON (`JsonElement.WriteTo` into a reused
`Utf8JsonWriter`). The static schema-text map is gone, not value-keyed: the
only stable value a `JsonDocument` carries is its own raw text, and reading
that text back out is the per-turn cost the map existed to avoid.

| Test | Relative claim | Gate | Measured |
|---|---|---|---|
| `KeyCost_GrowsLinearly_InContextSize` | time + allocations over `BuildAsync` with a context file of 32 KiB → 64 KiB, one fresh-key turn per unit, inner builder faked out so the window is the key pass | ratio ≤ 3.0 both branches (linear ≈ 2.0; the joined-string regression is 4.0) | **1.90** time (80.945 → 153.602 ms, medians of 5 legs of 2233 units), **1.00** allocations (2 197 496 B on both legs — flat in N) — run 39125259218, PR #1692 |
| `BuildAsync_SameContentsTwice_ServesOneBuild` | hit-rate assertion the issue asked for: same 256 KiB context twice → one `ComputeKey` result, one inner build, `CacheHits == 1` | exactly one build | asserted in-test |
| `BuildAsync_SameSchemaDifferentWhitespace_ServesOneBuild` | value-key proof the reference map could never give: same schema tree with different literal whitespace → same key → one build | exactly one build | asserted in-test |
| `BuildAsync_DifferentSchemaValue_RebuildsPrompt` | negative control for the assert above (a constant-hit unchanged comparison cannot serve a stale schema) | two builds | asserted in-test |

The BDN absolute columns live in the `benchmark` workflow (`CachingPromptBenchmark`,
`Build_Hit` / `Build_Miss`, `--job Short`); key computation on the hit row is
dominated by the `ComputeKey` pass either way, so the PR-marginal claim moves
with the ratio above, not with those absolute numbers.

### ProjectHistory first-occurrence map (#1535) — **PER-UNIT BUDGET**, CI merge gate

`Harbor.Ui.Framework.Tests.ProjectHistoryScalingGates`, in the `test (ui)` merge-gate shard.

The slice: `DefaultUiProjector.ProjectHistory` rebuilt a
`Dictionary<ChatLine,int>` over the WHOLE transcript on every history change —
O(history) dictionary + N hash inserts per fold, per frame batch — even for a
pure append (the `MessageEnd` fold), whose unchanged prefix the map already
covered. The map is now cached against `cache.Lines` (the same key the
common-prefix reuse is keyed on) and extended over only the appended lines;
the full rebuild stays for a changed prefix, where entries beyond the prefix
would answer stale first-occurrence indices. First-equal-wins `BlockId`
semantics unchanged — pinned against a reference mapper through appends, the
tool-call key, and a `HydrateSession` rewrite in the same file.

The window is measured APPENDS at approximately-fixed transcript size, seeded
unmeasured — deliberately not a growth-from-empty drive: the common-prefix
scan and the per-fold builders are O(n) per change too and are OUT of this
slice (#97 slices them separately), so a growth drive would show ×4 to the
correct tree and read the #939 shape, a bound that fails on right numbers.

**The gate is a BUDGET PER UNIT OF WORK since #1747, not a ratio.** It used to
be `time(2N)/time(N) ≤ 3` and `alloc(2N)/alloc(N) ≤ 3` over 512 against 1024
transcript lines, decided from best-of-5 legs with no GC fence and no leg
scaling — and it reddened on a byte-identical tree at **time ×3.10 while alloc
×1.99** (#1285, run 38129838108). The allocation leg said the tree was fine;
the time leg said otherwise; the tree was the same. A ratio's denominator is
the same order of magnitude as its numerator, so a scheduling wobble moves it
by whole multiples, and its base is not a fixed amount of work.

| Test | Claim | Gate | Measured |
|---|---|---|---|
| `AppendOnly_CostPerChange_StaysWithinBudget` | allocation per append+`Project` pair at 1024 transcript lines — the size where a per-change cost that grows with the transcript shows up | ≤ 128 KiB per pair (machine-independent, HARD) | **98 547 B** per pair (run 39372950798, PR #1807) — the same number the pre-#1747 ratio gate measured (12 318.3 KiB over 128 pairs) |
| `AppendOnly_CostPerChange_StaysWithinBudget` | time per append+`Project` pair, in **probe units** — the cost of one pair divided by the cost of one unit of the fixed calibration probe measured on the same runner in the same run | ≤ 1200 probe units per pair (machine-scaled, SOFT — stands down when the measurement is not trusted) | **297** probe units per pair — 0.118 ms/unit against a probe that costs 0.000398 ms/unit (run 39372950798) |

The byte budget is **pinned from the CI measurement** — 98 547 B per pair at
1024 transcript lines, which is the same number the pre-#1747 ratio gate
measured (12 318.3 KiB over 128 pairs) — so the budget leaves ~1.33× headroom,
and a per-append full-map rebuild (the regression this gate exists for) lands
several times above it.

> The `[perf-gate] project-history-cost: measured 128 unit(s) …` report line
> prints into the `test (ui)` job log on every run — cite THAT run's values
> when filling the Measured column, never the local absolute milliseconds.

### Sqlite append cost gate (#1534) — **PER-UNIT BUDGET**, CI merge gate

`Harbor.Storage.Tests.SqliteAppendCostGate`, in the `test (platform)` merge-gate shard.

The slice: every appended row used to pay 2× `ToString("O")` (the message
stamp plus the `session.updated_at` touch) and one `AddWithValue` per
parameter, with each value-type argument (`long created_at_ms`) boxing and
having its `SqliteType` inferred from the runtime type on every call.

**The gate is a BUDGET PER UNIT OF WORK since #1747, not a ratio.** It used to
be `cost(2N)/cost(N) ≤ 3` for time and for allocations over 1024 against 2048
appended messages, decided from best-of-3 legs with no GC fence, no leg
scaling and a per-thread allocation counter read across `await`s. It is the
gate the #1747 inventory names as the receipt for "a ratio depends on the base
and on the noise": **it reddened on a loaded runner at "ratio 3.102 vs limit
3.00"** — a 3% overshoot on a tree nobody had touched.

| Test | Claim | Gate | Measured |
|---|---|---|---|
| `Append_CostPerMessage_IsWithinBudget` | allocation per appended message, over a FIXED 512 appended messages into a session that already holds 512 rows — so a cost that grows with the session's size lands in the measurement | ≤ 16 KiB per message (machine-independent, HARD) | **7 816 B** per message (run 39372950798, PR #1807) |
| `Append_CostPerMessage_IsWithinBudget` | time per appended message, in **probe units** — the cost of one append divided by the cost of one unit of the fixed calibration probe measured on the same runner in the same run | ≤ 1500 probe units per message (machine-scaled, SOFT) | **444** probe units per message — 0.169 ms/unit against a probe that costs 0.000380 ms/unit (run 39372950798) |

The byte budget's reference point is `docs/BENCHMARKS.md`'s own row — "Sqlite
WAL Append ×10 | 2.2–2.6 ms | 155 KB", i.e. ~15.5 KB per append — with 2×
headroom for the runtime's own bookkeeping inside the measured window. The
The shape this gate was written for (#1534) cost two extra `ToString("O")` strings
and two boxes per row; the report line prints the measured number on every
run, so the constant can be tightened against what the runner actually does.

The measurement uses the PROCESS-WIDE allocation counter and a keyless
`[NotInParallel]`: the append path awaits, and a per-thread counter sampled
across a thread hop is the difference between two unrelated counters (#741);
the keyless attribute is the only form that actually delivers "nothing else in
this assembly allocates while the window is open".

### Per-event string shredding — the TUI event path (#1139) — **PER-UNIT BUDGET**, CI merge gate

`Harbor.Tui.CellForge.Tests.EventStringShreddingGates`, in the `test (ui)` merge-gate shard.

The slice (issue #1139, slice 3 — slices 1 and 2 landed via #1134 and #1143): the
CellForge event path copied whole strings where a span would do, and the copies
were paid per event and per frame.

| Site | The copy | Paid |
|---|---|---|
| `ToolCardTracker.EnsureCard(JsonElement)` | the args JSON (`GetRawText`) plus the summary, shaped for a card that ALREADY EXISTS — the LLM's `ToolCallStart` opens the card first and the string overload discards the shaped args | per tool call |
| `ToolCallBlock` measure + paint | `Output.TrimEnd('\n')` — a fresh copy of the WHOLE tool body, three times per frame (a command's stdout always ends with a newline, so the trim always copies) | per frame, per card |
| `HunkVirtualizer.SplitPatchHunks` | `Replace("\r\n")` + `Replace('\r')` over the whole patch body, a `string[]` of the lines, and a `Replace("\t", "  ")` per line — four copies of the body | per frame, per change |

**The gate is a BUDGET PER UNIT OF WORK (#1747), not a ratio.** A byte budget is
machine-independent and is compared as measured, with no band; the time budget is
in **probe units** — the cost of one unit of work divided by the cost of one unit
of the fixed calibration probe measured on the SAME runner in the SAME run — so the
runner's speed cancels. The time branch asserts only when the measurement is
trusted (reproducible spread, long enough leg); otherwise it reports and stands
down.

| Test | Claim | Gate | Measured |
|---|---|---|---|
| `ToolStart_On_An_Open_Card_CostsWithinBudget` | allocation per tool-start event on a card the LLM already opened | ≤ 8 B per event (machine-independent, HARD) | **104.0 B → 0.0 B** per event (before run 39390539021 → after run 39399621327) — the shaped args are gone, and the card is a dictionary probe |
| `ToolStart_On_An_Open_Card_CostsWithinBudget` | time per tool-start event, in **probe units** — the cost of one event divided by the cost of one unit of the fixed calibration probe measured on the same runner in the same run | ≤ 1.0 probe units per event (machine-scaled, SOFT — stands down when the measurement is not trusted) | **0.5 → 0.2** probe units per event (before run 39390539021 → after run 39399621327) |
| `ToolCardFrame_CostsWithinBudget` | allocation per painted frame of a timeline carrying a 64 KiB tool card | ≤ 4 KiB per frame (machine-independent, HARD) | **131 144 B → 0.0 B** per frame (before run 39390539021 → after run 39399621327) — the frame paints spans, and the body is not copied at all |
| `ToolCardFrame_CostsWithinBudget` | time per painted frame, in **probe units** | ≤ 700 probe units per frame (machine-scaled, SOFT) | **339.1 → 299.1** probe units per frame (before run 39390539021 → after run 39399621327) |
| `PatchSplit_CostsWithinBudget` | allocation per split of a 1000-line CRLF patch with tabbed lines | ≤ 128 KiB per split (machine-independent, HARD) | **175 248 B → 93 336 B** per split (before run 39390539021 → after run 39399621327) — the remaining bytes are the lines themselves, which are the API's result |
| `PatchSplit_CostsWithinBudget` | time per patch split, in **probe units** | ≤ 1400 probe units per split (machine-scaled, SOFT) | **682.7 → 505.2** probe units per split (before run 39390539021 → after run 39399621327) |

The BEFORE column is the run of the **gate-only commit** (the first commit of
PR #1910): the gate landed before the fix on purpose, because a budget pinned
from a tree that still shreds strings says nothing about the fix. Every budget
above sits below that measured cost — so the pre-slice shape fails the gate — and
above what the slice leaves behind.

The gate earned its keep on the way: the first cut of the patch split expanded
each line's tabs through a `StringBuilder`, and the measurement of that cut read
**208 936 B per split** — *more* than the shape it replaced (a StringBuilder per
line costs the object, its chunk buffer and the string it hands back). The
expansion now goes through a pooled buffer, and the commit that fixes it says so.

> The `[perf-gate] tool-start-event-cost: measured …` report lines print into
> the `test (ui)` job log on every run, and `tools/test-measurements.py` lifts
> them into the job summary (#618) — cite THAT run's values when filling a
> Measured column, never local absolute milliseconds.
### CellForge per-frame snapshot gate (#1140) — **PER-UNIT BUDGET**, CI merge gate

`Harbor.Tui.CellForge.Tests.FrameSnapshotCostGate`, in the `test (ui)` merge-gate shard.

The slice: the CellForge renderer paints a frame by walking the layout tree
and painting every panel, and the surfaces below rebuilt a snapshot of their
own state on **every** frame, whether or not anything had changed. The
session tab strip derived its whole plan again per painted frame (two scratch
`int[]` arrays, an `ImmutableArray<TabCell>` builder, a cell array, and a
cleaned + truncated title string per visible tab); the toast overlay built its
row as a fresh string per toast per frame; the overlay stack allocated two
`List<Rect>` scratch lists per painted frame; the sidebar's session row was a
fresh string per visible session per frame.

The tab-strip plan is now memoised against (the strip snapshot, the width) —
the snapshot is an immutable value the reducer replaces only when a tab
really changes — and the rows are written into stack buffers.

**The gate is a BUDGET PER UNIT OF WORK since #1747, not a ratio.** The unit
is ONE PAINTED FRAME of the surface, so the budget is a constant number of
bytes per frame: machine-independent, so it is compared as measured, with no
band — the branch that can be a hard gate on any runner. The time budget is
in **probe units** (the cost of one frame divided by the cost of one unit of
the fixed calibration probe measured on the same runner in the same run), so
the runner's speed cancels and the budget is a constant in a
machine-independent currency.

| Test | Claim | Gate | Measured |
|---|---|---|---|
| `TabStrip_SteadyStateFrame_CostPerFrame_StaysWithinBudget` | allocation per painted tab-strip frame — 6 open tabs at 80 columns, one panel painted 2 000 times | ≤ 256 B per frame (machine-independent, HARD) | **0 B** per frame (run 39399184283, PR #1908) — **before: 2 688 B** per frame, the same instrument on the pre-#1140 tree (run 39391746836) |
| `TabStrip_SteadyStateFrame_CostPerFrame_StaysWithinBudget` | time per painted tab-strip frame, in **probe units** | ≤ 5 000 probe units per frame (machine-scaled, SOFT — stands down when the measurement is not trusted) | **7.2** probe units per frame — 0.0027 ms/frame against a probe that costs 0.000380 ms/unit, spread 1.24×, trusted (run 39399184283); before: **21.5** probe units per frame — 0.0082 ms/frame, spread 1.32×, trusted (run 39391746836) |
| `ToastOverlay_SteadyStateFrame_CostPerFrame_StaysWithinBudget` | allocation per painted overlay frame — one toast in the overlay stack, painted 2 000 times | ≤ 128 B per frame (machine-independent, HARD) | **0 B** per frame (run 39399184283, PR #1908) — **before: 560 B** per frame, the same instrument on the pre-#1140 tree (run 39391746836) |
| `ToastOverlay_SteadyStateFrame_CostPerFrame_StaysWithinBudget` | time per painted overlay frame, in **probe units** | ≤ 5 000 probe units per frame (machine-scaled, SOFT) | **3.4** probe units per frame — 0.0013 ms/frame against a probe that costs 0.000380 ms/unit (run 39399184283); before: **3.8** probe units per frame — 0.0015 ms/frame (run 39391746836). Both legs stood down on the time branch — the toast leg's spread was 1.60× over 5 rounds, above the 1.50× trust ceiling — so the time number is reported, not asserted |

The byte ceilings are the regression line, not a description of the cost: the
measured value on the fixed tree is **0 B per painted frame** for both
surfaces — the strip paints from the memoised plan and the toast from a
stack buffer, and neither allocates in the steady state — while the same
instrument on the pre-#1140 tree measured 2 688 B and 560 B per frame and
**failed both ceilings**. So the gate is red on the tree the slice fixes and
green on the tree it leaves, on the same runner and in the same run.

Two positive controls keep the byte budget from being satisfied by measuring
nothing (#591):

- `TabStrip_ChangedStripFrame_PaysTheLayoutTheMemoRemoves` — a frame on
  which the tab set changed (a fresh strip snapshot) still pays the whole
  layout: **2 480 B** per changed frame against a floor of 96 B (run
  39399184283). So the work the memo removes from the quiet frames is a real
  allocation of a known size, and the quiet-frame budget is a budget on that
  work — not on nothing.
- `ToastRow_RemovedStringBuild_WasARealAllocation` — the removed row build,
  verbatim, measured in isolation: **360 B** per row build against a floor
  of 48 B. The buffer that replaced it removed something real.

> The `[perf-gate] cellforge-tabstrip-frame: measured …` report line prints
> into the `test (ui)` job log on every run — cite THAT run's values when
> filling the Measured column, never the local absolute milliseconds.

### The ratio-form gates become per-unit budgets (#1948) — **PER-UNIT BUDGET**, CI merge gate

Fifteen gates move from the RELATIVE form to the #1747 form: a **budget per unit
of work**, with the amount of work FIXED, the allocation branch
machine-independent and hard, the time branch in **probe units** and soft.

**Why the relative form had to go.** A ratio's denominator is the same order of
magnitude as its numerator, so runner noise moves it by whole multiples, and
its base is not a fixed amount of work — so the number the gate reads depends
on the size the gate happened to pick. The receipt is run 39423151864, where
`ToolDispatcherToolContextScalingTests` read **time x4.69 while allocations read
x1.99** on a tree nobody had touched: textbook linear memory with an inflated
stopwatch. The 7 failures in 151 `core` runs (4.6 %) were all of that shape.

Two of the gates were not merely noisy but **measuring the harness**:

- `ToolDispatcherToolContextScalingTests` — the fake event bus keeps every
  published event forever, so the GC's marking cost per dispatch grows with the
  dispatch count. The 2N leg paid for a bigger retained pile, and the ratio
  measured the RETENTION, not the dispatcher. The pile is now cleared between
  dispatches.
- `AgentReducerToolBoundaryTests` — the two ratio legs were **the same
  measurement** (`ToolBoundaryLeg(units)` on both sides), so the ratio was
  noise around 1.00 and the gate could not fail on the quadratic behaviour it
  was written for.

| Test | Claim | Old gate | New gate | Measured (run 39423151864) |
|---|---|---|---|---|
| `ToolDispatcherToolContextScalingTests.ToolContextBuilds_CostPerToolCall_StaysWithinBudget` | allocation per tool call, over a FIXED 50 000 calls | ratio `alloc(2N)/alloc(N)` ≤ 3.0 (and `time(2N)/time(N)` ≤ 3.15) | **≤ 4 000 B per call** (machine-independent, HARD) | **2 382 B** per call (135 563 152 B over 569 dispatches of 100 calls) |
| `ToolDispatcherToolContextScalingTests.ToolContextBuilds_CostPerToolCall_StaysWithinBudget` | time per tool call, in **probe units** — the cost of one call divided by the cost of one unit of the fixed calibration probe measured on the same runner in the same run | ratio, above | **≤ 60 probe units per call** (machine-scaled, SOFT) | **~2.2 probe units** per call — 0.828 µs/call against a probe that costs 0.000380 ms/unit |
| `ToolDispatcherResolveOnceTests.ExecuteAsync_CostPerToolCall_StaysWithinBudget` | allocation per tool call, over a FIXED 25 000 calls | ratio ≤ 3.0 / 3.15 | **≤ 3 500 B per call** (HARD) | **2 079 B** per call (58 421 064 B over 281 dispatches of 100 calls) |
| `ToolDispatcherResolveOnceTests.ExecuteAsync_CostPerToolCall_StaysWithinBudget` | time per tool call, in probe units | ratio, above | **≤ 175 probe units per call** (SOFT) | **~6.4 probe units** per call — 2.44 µs/call |
| `CachingSystemPromptBuilderKeyScalingTests.KeyCost_PerTurn_StaysWithinBudget` | allocation per turn's key computation, over a FIXED 1 000 turns at the LARGER context | ratio ≤ 3.0 / 3.15 | **≤ 1 536 B per turn** (HARD) | **984 B** per turn (2 954 192 B over 3 002 turns) — and the **same** number at twice the context size, which is the point: the key streams the context through one bounded buffer |
| `CachingSystemPromptBuilderKeyScalingTests.KeyCost_PerTurn_StaysWithinBudget` | time per turn's key computation, in probe units | ratio, above | **≤ 2 400 probe units per turn** (SOFT) | **~171 probe units** per turn — 65.0 µs/turn at 65 536 chars |
| `TurnRunnerToolTableReuseTests.RunAsync_CostPerTurn_StaysWithinBudget` | allocation per text-only turn, over a FIXED 1 000 turns against a 200-tool registry | ratio ≤ 3.0 / 3.15 | **≤ 30 000 B per turn** (HARD) | **23 010 B** per turn (34 055 168 B over 1 480 turns) |
| `TurnRunnerToolTableReuseTests.RunAsync_CostPerTurn_StaysWithinBudget` | time per text-only turn, in probe units | ratio, above | **≤ 600 probe units per turn** (SOFT) | **~205 probe units** per turn — 77.8 µs/turn |
| `TurnRunnerUpdateCoalescingTests.PublishCost_PerDelta_StaysWithinBudget` | allocation per published delta, over a FIXED 1 000 000 deltas | ratio ≤ 3.0 / 3.15 | **≤ 92 B per delta** (HARD) | **72 B** per delta (147 649 968 B over 1 024 turns of 2 000 deltas) |
| `TurnRunnerUpdateCoalescingTests.PublishCost_PerDelta_StaysWithinBudget` | time per published delta, in probe units | ratio, above | **≤ 6 probe units per delta** (SOFT) | **~0.116 probe units** per delta — 0.044 µs/delta |
| `ToolRegistryDescribeOnceTests.ResolveTools_CostPerResolve_StaysWithinBudget` | allocation per first-time filtered resolve, over a FIXED 100 000 resolves | ratio ≤ 3.0 / 3.15 | **≤ 1 500 B per resolve** (HARD) | **984 B** per resolve (98 400 000 B over 100 000 resolves) |
| `ToolRegistryDescribeOnceTests.ResolveTools_CostPerResolve_StaysWithinBudget` | time per first-time filtered resolve, in probe units | ratio, above | **≤ 40 probe units per resolve** (SOFT) | **~1.7 probe units** per resolve — 0.645 µs/resolve |
| `JsonlMessagePayloadScalingTests.SerializeMessagePayload_CostPerMessage_StaysWithinBudget` | allocation per serialized message, over a FIXED 1 000 000 messages | ratio ≤ 3.0 / 3.15 | **≤ 220 B per message** (HARD) | **145 B** per message (49 788 976 B over 21 387 batches of 16) |
| `JsonlMessagePayloadScalingTests.SerializeMessagePayload_CostPerMessage_StaysWithinBudget` | time per serialized message, in probe units | ratio, above | **≤ 6 probe units per message** (SOFT) | **~0.115 probe units** per message — 0.044 µs/message |
| `SessionFileReaderScalingTests.ReadPath_CostPerRead_StaysWithinBudget` | allocation per full session-file read, over a FIXED 8 reads of a 4 000-message file | ratio ≤ 3.0 / 3.15 | **≤ 1 600 000 B per read** (HARD) | **1 275 659 B** per read (16 583 576 B over 13 reads) |
| `SessionFileReaderScalingTests.ReadPath_CostPerRead_StaysWithinBudget` | time per full session-file read, in probe units | ratio, above | **≤ 300 000 probe units per read** (SOFT) | **~25 200 probe units** per read — 9.59 ms/read |
| `GenericLiveRegionTests.Region_Paint_CostPerFrame_StaysWithinBudget` | allocation per painted frame, over a FIXED 400 paints of a 400-line region | ratio ≤ 3.15 (time only) | **≤ 0 B per frame** (HARD — the exact zero, the strongest shape a byte claim can take) | **0 B** per frame |
| `GenericLiveRegionTests.Region_Paint_CostPerFrame_StaysWithinBudget` | time per painted frame, in probe units | ratio, above | **≤ 4 500 probe units per frame** (SOFT) | **~312 probe units** per frame |
| `AgentReducerToolBoundaryTests.ToolBoundary_CostPerPair_StaysWithinBudget` | allocation per tool-start/end pair, over a FIXED 4 000 pairs | ratio ≤ 3.0 — **the same measurement on both sides** | **≤ 16 000 B per pair** (HARD) | **11 959 B** per pair (208 151 840 B over 17 404 pairs) |
| `AgentReducerToolBoundaryTests.ToolBoundary_CostPerPair_StaysWithinBudget` | time per tool-start/end pair, in probe units | ratio, above | **≤ 200 probe units per pair** (SOFT) | **~9.6 probe units** per pair — 3.64 µs/pair |
| `BashArgMatcherScalingGates.Cost_PerSplit_StaysWithinBudget` | allocation per argv split of the 512-token command, over a FIXED 20 000 splits | ratio ≤ 3.0 / 3.15 | **≤ 56 000 B per split** (HARD) | **38 000 B** per split (1 520 000 040 B over 40 000 splits) |
| `BashArgMatcherScalingGates.Cost_PerSplit_StaysWithinBudget` | time per argv split, in probe units | ratio, above | **≤ 250 000 probe units per split** (SOFT) | **~17 100 probe units** per split — 6.51 µs/split |
| `SsePumpScalingGates.Pump_CostPerPass_StaysWithinBudget` | allocation per pump pass over the 192-data-line stream, over a FIXED 100 passes | ratio ≤ 3.0 / 3.15 | **≤ 2 300 000 B per pass** (HARD) | **1 583 905 B** per pass (885 503 064 B over 559 passes) |
| `SsePumpScalingGates.Pump_CostPerPass_StaysWithinBudget` | time per pump pass, in probe units | ratio, above | **≤ 15 000 probe units per pass** (SOFT) | **~980 probe units** per pass — 373 µs/pass |
| `OpenAiCompatibleRequestBuildScalingTests.BuildRequest_CostPerBuild_StaysWithinBudget` | allocation per request build over the 400-tool-call request, over a FIXED 500 builds | ratio ≤ 3.0 / 3.15 | **≤ 320 000 B per build** (HARD) | **227 288 B** per build (230 291 408 B over 1 013 builds) |
| `OpenAiCompatibleRequestBuildScalingTests.BuildRequest_CostPerBuild_StaysWithinBudget` | time per request build, in probe units | ratio, above | **≤ 6 800 probe units per build** (SOFT) | **~453 probe units** per build — 180 µs/build |
| `MatchesPatternScalingGates.Cost_PerCall_StaysWithinBudget` | allocation per `Evaluate` miss-walk, over a FIXED 2 400 calls against the 512-rule ruleset | ratio ≤ 3.0 / 3.15 | **≤ 0 B per call** (HARD — the exact zero) | **0 B** per call (876 361 ms / 0 B over 40 000 calls) |
| `MatchesPatternScalingGates.Cost_PerCall_StaysWithinBudget` | time per `Evaluate` miss-walk, in probe units | ratio, above | **≤ 3 000 probe units per call** (SOFT) | **~110 probe units** per call at 512 rules — 43.8 µs/call against a probe that costs 0.000380 ms/unit |
| `StorePathScalingGates.Cost_PerDelta_StaysWithinBudget` | allocation per scripted delta, over a FIXED 100 000 deltas | ratio ≤ 3.0 / 3.15 | **≤ 1 700 B per delta** (HARD) | **1 300 B** per delta (71 508 360 B over 55 drives of 1 000 deltas) |
| `StorePathScalingGates.Cost_PerDelta_StaysWithinBudget` | time per scripted delta, in probe units | ratio, above | **≤ 13 probe units per delta** (SOFT) | **~0.87 probe units** per delta — 0.331 µs/delta |

**The probe-unit currency is only as stable as the yardstick behind it, and
that is a measured finding, not a caveat.** The probe's OWN measured cost moved
**5.3x** between two CI runs of the SAME tree: **0.000380 ms/unit** in run
39423151864 and **0.000072 ms/unit** in run 39436405496. The consequence is
visible in the receipt: `TurnRunnerUpdateCoalescingTests` read **0.116 probe
units per delta** in the first run and **1.27** in the second - an 11x move -
while its BYTE branch read **72 B per delta in both**, to the byte. So the byte
budgets above are the branch that can fail on any runner (they are exact, and
they are what caught the quadratic shapes), and the probe-unit budgets are the
gross-regression detector, sized with an order-of-magnitude envelope rather
than the 2-4x the house's earlier gates carried. Making the yardstick itself
stable - a probe that is always measured in the same JIT tier, and a
process-wide minimum over samples instead of a median - is the follow-up, and
it has to retune the two gates that already ride the helper
(`SqliteAppendCostGate`, `ProjectHistoryScalingGates`), so it is its own
change.

**The wall time, measured.** Same shard, same test counts, the CI run before the
conversion (39423151864) and the CI run after it (39453986590) — the suite
durations are the job log's own "Test run summary: … duration" line:

| Suite | Before | After |
|---|---|---|
| `Harbor.Abstractions.Tests` (257 tests) | 18.542 s | **4.014 s** — the `MatchesPatternScalingGates` leg-length fix |
| `Harbor.Application.Tests` (578 tests) | 12.009 s | **6.927 s** |
| `Harbor.Storage.Jsonl.Tests` | 7.499 s | **5.164 s** |
| `Harbor.Ui.Framework.Tests` | 6.840 s | **5.660 s** |
| `Harbor.Registries.Tests` | 2.648 s | **2.166 s** |
| `Harbor.Domain.Tests` | 3.117 s | **2.464 s** |
| `Harbor.Providers.Tests` | 5.572 s | **1.509 s** |
| `Harbor.Tui.CellForge.Tests` | 8.472 s | 8.216 s |

**The leg length is FIXED now, and that is where the wall time went.** The
shared helper's leg SCALER raised the unit count until a leg lasted ~100 ms, so
the faster the runner the FEWER units a leg carried — and the more the verdict
moved with the machine. `StorePathScalingGates` swung between **3.6 s and
27.4 s** across runs (the largest variance source in the `ui` shard, 17 % of its
wall) and `MatchesPatternScalingGates` paid **876–1 838 ms per leg against a
100 ms target**: its scaler started at `initialUnits = CallsPerWindow = 200`,
and one unit was already a burst of 200 `Evaluate` calls, so the leg was 8–17×
the target on every probe, every confirmation, the warm-up and all five rounds.
26.5 s of that gate's 27.9 s went to proving a ratio that never moved
(x2.03–x2.21 across seven runs). Its scaler now starts at ONE unit; the gate's
FORM is still a ratio, because converting it needs `Harbor.TestKit.PerfGate`,
which `Harbor.Abstractions.Tests` cannot reach until #1889's TestKit split
gives it the TestKit reference.

**The control still fails.** No gate is disabled. The store path's sublinearity
claim is the COUNT gate beside it (`FullProjections ≤ folds + 2`,
`TailRebuilds ≤ policyFlushes + streaming transitions`, `Materializations ≤
deltas/10`, `LineRestyles == 0`), every counter of which is shown non-zero
before it is bounded; the byte branch of every converted gate refuses a zero
measurement; and the time branch stands down only when the rounds do not
reproduce each other, reporting either way.

> The `[perf-gate] …: measured …` report lines print into the shard job log on
> every run, and `tools/test-measurements.py` lifts them into the job summary
> (#618) — cite THAT run's values when filling a Measured column, never local
> absolute milliseconds.

### Perf-gate inventory (#1747) — every gate, one verdict

The discipline #1747 asks for, in one table: **measure → report → assert
softly**; **scale only time** (bytes are a machine-independent constant);
**budget = a constant per unit of work**, not a "≤ 3×" ratio. The verdicts use
four words: **keep** (the gate already has the right shape), **convert to a
constant** (a budget per unit of work), **convert to a relative share of
work** (a fraction of a same-run reference), **replace the methodology** (the
measurement, not the claim, is what is wrong).

| Gate | Shape today | Compares | Verdict |
|---|---|---|---|
| `Harbor.Ui.Framework.Tests.StorePathScalingGates.Cost_GrowsSubLinearly_InDeltas` | ratio | `time(2N)/time(N)` and `alloc(2N)/alloc(N)` ≤ 3.0 over 1000/2000 deltas; GC-fenced legs, best-of-3 | **convert to a constant** — done in #1948. Its `TheRatioRuleAnswersTheDeclaredQuestion` control stays, now pinning the SHARED ratio rule the other ratio gates still ride; this file's own control is the COUNT gate beside it |
| `Harbor.Ui.Framework.Tests.ProjectHistoryScalingGates` | ratio | `time(2N)/time(N)` ≤ 3.0 over 512/1024 transcript lines; best-of-5, no fence, no leg scaling | **convert to a constant** — done in #1747. This was the ×3.10 red (#1285) |
| `Harbor.Storage.Tests.SqliteAppendScalingGate` | ratio | `time(2N)/time(N)` ≤ 3.0 over 1024/2048 appends; best-of-3, no fence | **convert to a constant** — done in #1747, as `SqliteAppendCostGate`. This was the "ratio 3.102 vs limit 3.00" red |
| `Harbor.Tui.CellForge.Tests.FrameSnapshotCostGate` | absolute bytes per painted frame | ≤ 256 B / ≤ 128 B per painted tab-strip / overlay frame; time in probe units | **keep** — a constant budget per unit of work, in the house `PerfGate` shape. The pre-#1140 tree measures 2 688 B and 560 B per frame against it and fails both ceilings (run 39391746836); the fixed tree measures 0 B per frame on both (run 39399184283) |
| `Harbor.Application.Tests.ToolDispatcherResolveOnceTests` | ratio | ≤ 3.0, through `TestScalingGate` | **convert to a constant** — done in #1948 |
| `Harbor.Application.Tests.TurnRunnerToolTableReuseTests` | ratio | ≤ 3.0, through `TestScalingGate` | **convert to a constant** — done in #1948 |
| `Harbor.Application.Tests.CachingSystemPromptBuilderKeyScalingTests` | ratio | ≤ 3.0, through `TestScalingGate` | **convert to a constant** — done in #1948 |
| `Harbor.Core.Tests.CompactionPolicyMaterializeScalingTests` | ratio | ≤ 3.0, through `TestScalingGate`; the pre-#1533 shape runs report-only | **keep** — same |
| `Harbor.Storage.Jsonl.Tests.JsonlMessagePayloadScalingTests` | ratio | ≤ 3.0, through `TestScalingGate` | **convert to a constant** — done in #1948 |
| `Harbor.Storage.Jsonl.Tests.SessionFileReaderScalingTests` | ratio | ≤ 3.0, through `TestScalingGate`; the pre-#1533 shape runs report-only | **convert to a constant** — done in #1948; the pre-#1533 companion stays on the ratio helper because it never gated anything |
| `Harbor.Abstractions.Tests.MatchesPatternScalingGates` | ratio + exact zero | ≤ 3.0 through `TestScalingGate`; the allocation claim is **exactly 0 B**, not a ratio | **convert to a constant** — done in #1948, once #1889 gave this assembly the TestKit reference: **0 B per Evaluate call** (the exact zero, which is the strongest shape a byte claim can take) and 3 000 probe units per call. Its LEG LENGTH was fixed in the same change (the legs were 876–1 838 ms against a 100 ms target, and 26.5 s of the gate's 27.9 s went to proving a ratio that never moved) |
| `Harbor.Domain.Tests.BashArgMatcherScalingGates` | ratio | ≤ 3.0, through `TestScalingGate` | **convert to a constant** — done in #1948: 56 000 B per split (hard) and 250 000 probe units per split (soft) |
| `Harbor.Providers.Tests.OpenAiCompatibleRequestBuildScalingTests` | ratio | ≤ 3.0, through `TestScalingGate` | **convert to a constant** — done in #1948: 320 000 B per build (hard) and 6 800 probe units per build (soft) |
| `Harbor.Providers.Tests.SsePumpScalingGates` | ratio | ≤ 3.0, through `TestScalingGate` | **convert to a constant** — done in #1948: 2 300 000 B per pump pass (hard) and 15 000 probe units per pass (soft) |
| `Harbor.Application.Tests.ToolDispatcherToolContextScalingTests` | ratio | ≤ 3.0, through `TestScalingGate`; the 2N leg read **time x4.69 with alloc x1.99** (run 39423151864) because the fake event bus's retained event pile made the GC's marking cost grow with the dispatch count | **convert to a constant** — done in #1948; the harness's retained pile is cleared between dispatches |
| `Harbor.Registries.Tests.ToolRegistryDescribeOnceTests` | ratio | ≤ 3.0, through `TestScalingGate` | **convert to a constant** — done in #1948 |
| `Harbor.Storage.Jsonl.Tests.SessionFileReaderScalingTests` (read path) | ratio | ≤ 3.0, through `TestScalingGate` | **convert to a constant** — done in #1948 |
| `Harbor.Tui.CellForge.Tests.GenericLiveRegionTests` | ratio | ≤ 3.15, time only; red at 3.54 on a real CI run | **convert to a constant** — done in #1948: the byte branch is the exact zero the steady paint already pins |
| `Harbor.Ui.Framework.Tests.AgentReducerToolBoundaryTests` | ratio | ≤ 3.0, through `TestScalingGate` — and **the same measurement on BOTH sides**, so the ratio was noise around 1.00 and the gate could not fail | **convert to a constant** — done in #1948 |
| `Harbor.Storage.Jsonl.Tests.JsonlUnboundedAllocationTests.Update_HeaderEditOnABigSession_…` | differential | `(big − baseline) ≥ 0` **and** `< 2 MiB` | **replace the methodology** — done in #1747. The `≥ 0` floor was a FALSE premise: both operands come from one monotonic counter, so their difference can legitimately be negative (a saving), and the observed value was exactly that (−15 384 B). A broken measurement is a negative SINGLE window, and that shape is refused inside `AllocationProbe`. The ceiling is now a constant per MiB of bulk rewritten |
| `Harbor.Storage.Jsonl.Tests.JsonlUnboundedAllocationTests.Read_ThousandRecordSession_…` | absolute + blanket `[Retry(3)]` | `allocated < graph + fileBytes` — a ceiling ~1.5% under the measured number | **replace the methodology** — done in #1747. The measurement is now a minimum over three reads (the process-wide counter's noise only ever inflates a reading), the budget is a constant per record, and the blanket retry is gone |
| `Harbor.Storage.Jsonl.Tests.JsonlAllocationBudgetTests` | absolute bytes | ≤ 8 KB/line, ≤ 512 KB/read | **keep** — a byte budget is already machine-independent and already per unit of work |
| `Harbor.Registries.Tests.EventBusAllocationTests` / `ToolRegistryAllocationTests` (file `AllocationBudgetTests.cs`) | absolute bytes | 0 B on the fast paths, ≤ 1–2 KB/publish on the fan-out | **keep** — same |
| `Harbor.Tui.CellForge.Tests.AllocationBudgetTests` | absolute bytes | 0 B per event | **keep** — same |
| `Harbor.Tui.CellForge.Tests.EventStringShreddingGates` | absolute bytes + probe units | ≤ 8 B per tool-start event, ≤ 4 KiB per painted frame, ≤ 128 KiB per patch split; the time branch in probe units | **keep** — a per-unit byte budget with a probe-unit time budget, added by #1139 for the TUI event path |
| `Harbor.Tui.CellForge.Tests.PerfBudgetTests` | absolute bytes, time reported | 0 B per frame; frame time printed, never gated (#996) | **keep** — the model of the discipline |
| `Harbor.Ui.Framework.Tests.StoreDispatchAllocTests` | differential bytes | per-dispatch cost of the store, measured against a bare store | **keep** — machine-independent and per unit of work |
| `Harbor.Application.Tests.AgentLoopAllocationTests` | absolute bytes | ≤ 512 KB / ≤ 2560 KB per turn, each with a `≥ 0` floor (#741) | **keep** — same |
| `Harbor.Ui.Framework.Tests.StatusBarFitPerfTests` | ratio, self-calibrating | ratio < 8 / < 2.0, but only after the harness proves it can reproduce its own baseline (spread ≤ 1.15×) | **keep** — the model for the "assert softly" rule #1747 generalises |
| `Harbor.Tui.PerfTests.RendererBenchmarkTests.Backend_MeetsAbsoluteContract` | absolute time | events/s, p99 latency, allocation ceiling against a declared contract | **keep** — an absolute wall-clock contract is machine-dependent by construction, which is why the file already gates it strictly only under `HARBOR_PERF_BASELINE_STRICT=1` |
| `Harbor.Tui.PerfTests.RendererBenchmarkTests` relative tail-render gate | ratio | `t(2N) ≤ 3·t(N)` in the same run | **keep** — the runner's speed cancels |
| `Harbor.LoadTests.MultiSessionLoadTests` | counts, time report-only | admissions double exactly when sessions double; both stopwatches printed | **keep** — the model of "gate the counts, report the time" |

### Cell style on the diff path (#1524) — **accepted shape**, measured in CI

`tests/Harbor.Benchmarks/CellStyleBenchmark.cs`, in the `benchmark` workflow step
**Bench CellStyle shared view**; and
`tests/Harbor.Tui.CellForge.Tests/CellStyleSharedViewTests.cs`, in the
`test (ui)` shard (its report lines land in the TRX and in the job summary via
`tools/test-measurements.py`).

Issue #1524 named its own gate: apply a shared (flyweight) style representation
ONLY if the measurement shows a win relative to "distinct styles << cells in a
frame"; otherwise record the measurement and keep the accepted shape.

**The gate number — the ratio the issue asked for.** The chat frame the test
builds (200×50, user prompts + assistant markdown + tool calls, painted through
the real panels) carries **4 218 painted cells over 17 distinct styles = 248.1
cells per style**; the benchmark grid (200×50, 16 styles crossed with the row
index) reads **10 000 cells over 51 distinct styles = 196.1 cells per style**.
So the sharing ratio the issue predicted IS there, two orders of magnitude.

**And it buys nothing, because there is no per-access allocation to share
away.** `CellStyle` is a `readonly struct` (packed fg / bg / attrs) and
`Cell.Style` copies three fields out of the cell's own 16-byte layout. The
allocation column is the decisive one:

| Row | Mean (Job-NTRUNJ) | Allocated |
|---|---|---|
| Style access per cell (builds a CellStyle) | 9.579 µs / 10 000 cells = **0.96 ns per cell** | **0 B** |
| Style compare per cell (rebuilds both CellStyles) — the pre-#1524 shape | 11.259 µs = 1.13 ns per cell | **0 B** |
| Style compare per cell (packed fields, no CellStyle) — the #1524 shape | 11.423 µs = 1.14 ns per cell | **0 B** |

Run 39469499716 (workflow_dispatch on `perf/1524-cell-style-flyweight`),
`--job Short`, ubuntu-latest. The two compare rows are **inside each other's
noise** (11.259 µs ± 0.076 vs 11.423 µs ± 0.190; the ShortRun leg reads
11.635 vs 11.550 — the other way round), so the packed compare is neither a win
nor a loss: the JIT already compiles `Style == Style` into the same three
integer compares. A classic flyweight (one heap object per distinct style,
shared by the cells that use it) would **ADD** an allocation per distinct style
and would grow the cell past the 16-byte budget the celldiff protocol pins —
so the shared form of a style here is the **packed triple the cell already
carries**, and `Cell.HasSameStyle` (the packed-field compare) is the form that
materialises no `CellStyle` at all.

The test-side numbers, from the `test (ui)` shard: **0 B** over 100 000 style
reads on the 4 218-cell painted frame, and **0 B** over 200 full 200×50
repaints that use 16 distinct styles. The immutability that makes this safe is
pinned too: every `CellStyle` field is `readonly` and holds a value type, so
there is no shared instance a caller could mutate under another holder.

### The shared helper (#1747) — `Harbor.TestKit.PerfGate`

`tests/Harbor.TestKit/PerfGate.cs` is the ONE way to write a per-unit-of-work
performance gate. Projects that must not drag `Harbor.Application` in keep a
documented internal copy (grep `PerfGate.cs — #1747` under `tests/`).

It answers a different question from `TestScalingGate` (#1652), which asks
"does doubling the input cost at most 3×?" — a RATIO, whose denominator is the
same order of magnitude as its numerator (so runner noise moves it by whole
multiples) and whose base is not a fixed amount of work. `PerfGate` asks "how
much does ONE UNIT OF WORK cost, and is that within a reviewed constant?".

| Piece | What it does |
|---|---|
| **Budgets** | `MaxBytesPerUnit` — a byte count per unit of work. Machine-independent, so it is compared as measured, with no band. This is the branch that can be a hard gate on any runner. `MaxProbeUnitsPerUnit` — a time budget per unit of work, in PROBE UNITS |
| **Probe units** | The cost of one unit of work divided by the cost of one unit of a fixed, deterministic, allocation-free CPU probe measured on the SAME runner in the SAME run. The runner's speed appears in both operands and cancels, so the budget is a constant in a machine-independent currency instead of a number of milliseconds that only means something on the box that produced it. **Only time is scaled this way; bytes never are** |
| **Measure** | The workload runs `Rounds` times behind a GC fence; the MEDIAN of each series is the measurement, and the relative spread across rounds is kept |
| **Report** | The report line is printed on every run, PASS OR FAIL, and says `measured` explicitly. `tools/test-measurements.py` lifts it into the job summary (the #618 mechanism), so a gate that measures and reports without failing is still evidence |
| **Assert softly** | The allocation branch asserts hard. The time branch asserts ONLY when the measurement is TRUSTED — the spread across rounds is within `MaxTrustedSpread` and the leg was long enough to measure. A runner that cannot reproduce its own baseline is not evidence about the code, so the time branch reports and stands down instead of failing the build (the `StatusBarFitPerfTests` #487 precedent) |
| **Non-vacuity** | A leg that measured nothing is a failure, not a pass: a zero millisecond reading is refused before any budget is read, and a zero byte reading is refused too — a byte count of zero satisfies every ceiling, so a mis-wired probe would make the gate pass on any tree (#591). A workload that legitimately allocates nothing per unit opts out with `RequirePositiveBytes = false` |

### JsonlMessagePayload serialization gate (#1531) — **PER-UNIT BUDGET**, CI merge gate

**Converted in #1948** — the cost gate here is now a BUDGET PER UNIT OF WORK (bytes per unit, hard; probe units per unit, soft) over a FIXED amount of work. The rows below are the pre-conversion measurement, kept as the dated record; the current budgets are in [the #1948 section](#the-ratio-form-gates-become-per-unit-budgets-1948--per-unit-budget-ci-merge-gate).


`Harbor.Storage.Jsonl.Tests.JsonlMessagePayloadScalingTests`, in the `test (platform)` merge-gate shard.

Same relative convention as #410 / #1528: N against 2N inside one run, no absolute
budgets, measured through the shared `Harbor.TestKit.TestScalingGate` (#1652 —
GC-fenced legs scaled toward ~100 ms of real work, medians over five rounds,
x1.05 noise band on the time branch only). The slice differs from the
issue-#1528 exact-zero rule: `JsonlMessageCodec.SerializeMessagePayload` did
real per-message allocation work that is *linear*, so a ratio gate is the
honest shape here, not an exact-zero one.

**Before (code shape, per the #97 slice audit — the LINQ path had no bench row
of its own, so the "before" column is structural, not measured):** every
persisted assistant message paid a `Select(SerializePart).ToArray()` iterator +
an oversized growable array, every tool-result message a
`Select(new ToolResultEntry(...)).ToArray()` iterator + array, and every
assistant message two strings for `StopReason.ToString() + ToLowerInvariant()` —
on the append path of every turn. **After:** one pre-sized array per message,
zero extra strings (a constant `StopReason` switch), on-disk payload shapes
byte-identical (pinned by `JsonlMessagePayloadShapeTests`, including the
`"tooluse"` spelling).

| Test | Relative claim | Gate | Measured |
|---|---|---|---|
| `SerializeMessagePayload_DoubledMessages_StaysWithinGrowthLimit` | time over N → 2N alternating user/assistant/tool_result payload batches — leg-scaled, medians of 5 rounds, linux-gated | ratio ≤ 3.0 (linear ≈ 2.0; O(N²) ≈ 4.0) | ⏳ not yet measured |
| `SerializeMessagePayload_DoubledMessages_StaysWithinGrowthLimit` | allocations over the same batches — runner-independent half of the claim | ratio ≤ 3.0, strict (no band, #1652) | ⏳ not yet measured |

> The `[scaling] jsonl-payload-scaling: ...` report line prints into the
> `test (platform)` job log on every run — cite THAT run's values when filling
> the Measured column, never the local absolute milliseconds.

### Per-delta update coalescing (#1527) — **PER-UNIT BUDGET**, CI merge gate ✅ measured

**Converted in #1948** — the cost gate here is now a BUDGET PER UNIT OF WORK (bytes per unit, hard; probe units per unit, soft) over a FIXED amount of work. The rows below are the pre-conversion measurement, kept as the dated record; the current budgets are in [the #1948 section](#the-ratio-form-gates-become-per-unit-budgets-1948--per-unit-budget-ci-merge-gate).


`Harbor.Application.Tests.TurnRunnerUpdateCoalescingTests`, in the `test (core)` merge-gate shard.

Same relative convention as #410 / #1528 / #1531: N against 2N inside one run, no
absolute budgets, measured through the shared `Harbor.TestKit.TestScalingGate`
(#1652 — GC-fenced legs scaled toward ~100 ms of real work, medians over five
rounds, x1.05 noise band on the time branch only).

**Before (per streamed delta, structural):** the turn runner published one
`MessageUpdateEvent` per delta — one record allocation plus one
`IEventBus.PublishAsync` round-trip (a subscriber snapshot, an awaited fan-out,
a scrollback append) for every token of every turn, on a path whose consumers
redraw at frame rate. **After:** `UpdateCoalescer` buffers the deltas of a run
and publishes them as one merged delta under the `UpdateFlushPolicy` (32 deltas
/ 4096 characters / 100 ms). The merge is lossless: the published deltas,
concatenated, are exactly the streamed deltas, and the last delta of a turn is
published before the turn's end event.

| Test | Relative claim | Gate | Measured |
|---|---|---|---|
| `PublishCost_DoubledDeltas_StaysWithinGrowthLimit` | time over 1000 → 2000 deltas in one turn — leg-scaled, medians of 5 rounds, linux-gated | ratio ≤ 3.0 (linear ≈ 2.0; a merge that re-copies the accumulated text per delta is O(N²) ≈ 4.0) | **x1.73** — CI run 39391423885, `test (core)`, median of 5 legs of 1001 units (small 104.185 ms / 79 124 144 B, large 180.269 ms / 144 526 600 B) |
| `PublishCost_DoubledDeltas_StaysWithinGrowthLimit` | allocations over the same turns — runner-independent half of the claim | ratio ≤ 3.0, strict (no band, #1652) | **x1.83** — same run |
| `PublishCost_BatchedPublishes_AllocateLessThanPerDeltaPublishes` | the before/after itself: the same 2000 deltas published one by one (`UpdateFlushPolicy.PerDelta` — the pre-#1527 path) and in batches, 20 turns each | batched allocates strictly less than per-delta | **x3.03 less memory, x2.11 faster** — same run: 2000 deltas x 20 units — per-delta 15.885 ms / 8 759 864 B; batched 7.519 ms / 2 895 680 B |

> The `[scaling] turnrunner-update-coalescing-scaling: ...` report line prints
> into the `test (core)` job log on every run — cite THAT run's values when
> filling the Measured column, never the local absolute milliseconds. The
> before/after pair prints as `[coalescing] <deltas> deltas x <units> units —
> per-delta: … ; batched: …`. A **passing** gate's line lands in the shard's
> TRX (and the job summary), not in the console log — the console only shows a
> gate's line when it skips.

### Tool-context callback closures (#1529) — **PER-UNIT BUDGET**, CI merge gate ✅ measured

**Converted in #1948** — the cost gate here is now a BUDGET PER UNIT OF WORK (bytes per unit, hard; probe units per unit, soft) over a FIXED amount of work. The rows below are the pre-conversion measurement, kept as the dated record; the current budgets are in [the #1948 section](#the-ratio-form-gates-become-per-unit-budgets-1948--per-unit-budget-ci-merge-gate).


`Harbor.Application.Tests.ToolDispatcherToolContextScalingTests`, in the `test (core)` merge-gate shard.

Same relative convention as #410/#1528: N against 2N inside one run, no
absolute budgets, measured through the shared `Harbor.TestKit.TestScalingGate`
(#1652 — GC-fenced legs scaled toward ~100 ms of real work, medians over five
rounds, x1.05 noise band on the time branch only).

**Before (per call, structural):** `ToolDispatcher.CreateToolContext` wired
both `ToolContext` callbacks as inline async lambdas — one closure
display-class per lambda (progress captured `toolCall`/`eventBus`/`logger`,
Ask captured `toolCall`/`permissions`/`logger`) — 2 display-classes per tool
call on the per-tool-call hot path, on top of the `ToolContext` itself.
**After:** the bodies hoisted into one small sealed state object per call
(`ToolCallGates`, the issue's "small sealed state record" shape) whose two
bodies are static and take that record as an explicit state parameter, and
the two delegates bind to the record as their target — zero display-class
allocations per call, and `ToolContext`'s public delegate signatures
untouched. Behaviour 1:1 — §FP-003 progress await-and-log and the #201 B1 ask
fail-closed shape are pinned by `ToolDispatcherToolContextScalingTests` plus
`ToolDispatcherAskFailClosedTests`.

Per call, the allocation shape is therefore: `ToolContext` + one state record
+ the two delegates (the delegates are the `ToolContext`'s own public
contract, so they cannot be shared across calls without changing that
contract) — 4 objects per call, down from 5. The shape is pinned directly,
without a byte budget, by
`Callbacks_ShareOneStateObject_AndNoClosureDisplayClass`: both delegates must
carry the same target object, and that target must not be a
compiler-generated closure class.

**Why a second dispatcher gate and not a new label on the existing one:**
`ToolDispatcherResolveOnceTests.ExecuteAsync_DoubledBatch_StaysWithinGrowthLimit`
(label `dispatcher-dispatch-scaling`) already measures N against 2N on the
same dispatch path — but its tool is a no-op, so the **callback** this slice
changed is never entered. The #1529 gate drives a tool that reports progress
twice per call, so every measured call goes through the rebuilt progress
callback.

| Test | Relative claim | Gate | Measured |
|---|---|---|---|
| `ToolContextBuilds_DoubledBatch_StaysWithinGrowthLimit` | time over dispatching a 100 → 200 tool-call batch (one `ToolContext` build per tool call per dispatch) — leg-scaled, medians of 5 rounds, linux-gated | ratio ≤ 3.0 (linear ≈ 2.0; O(N²) ≈ 4.0) | **x2.64** — CI run 39365025268, `test (core)`, median of 5 legs of 512 units (small 61.564 ms / 133042216 B, large 162.570 ms / 264523816 B) |
| `ToolContextBuilds_DoubledBatch_StaysWithinGrowthLimit` | allocations over the same batches — runner-independent half of the claim | ratio ≤ 3.0, strict (no band, #1652) | **x1.99** — same run; linear is 2.00, so the dispatch path allocates in proportion to the batch |

> The `[scaling] dispatcher-tool-context-scaling: ...` report line prints into
> the `test (core)` job log on every run — cite THAT run's values when filling
> the Measured column, never the local absolute milliseconds. A **passing**
> gate's line lands in the shard's TRX (and the job summary), not in the
> console log — the console only shows a gate's line when it skips.

### ConsoleEx cell-diff core (`DiffEngineBenchmark`, 2026-08-26, Release)

Frame-budget targets from `specs/07-tui.md` (< 16 ms/frame) and celldiff §7 — all met with an order of magnitude of headroom. Flush = real DiffEngine scan + AnsiWriter SGR/cursor encoding into a discarding backend (no tty I/O).

| Benchmark | Mean | Allocated | Target (celldiff §7) |
|---|--:|--:|--:|
| Idle frame, row-hash skip, 200×50 | **51.3 µs** | 0 B | ~0.05 ms ✅ |
| Token frame (~300 changed cells), 200×50 | **52.8 µs** | 0 B | ≤ 1 ms ✅ |
| Full repaint 200×50 | **137 µs** | 0 B | — |
| Full repaint 400×120 | **665 µs** | 0–184 B¹ | ~6 ms ✅ |
| Layout cold solve, 20 panels | **0.72 µs** | 184 B | < 0.01 ms ✅ |

¹ Allocation comes solely from the layout solver's cache-replay snapshot; all diff/encode paths are zero-alloc steady-state.

> All zero-allocation benchmarks (`0 B`) confirm the `ArrayPool` / `StringBuilderPool` / `FrozenDictionary` / `StringPool` strategy is working. The `JsonlSessionStore.GetMessages` row above predates the `Utf8JsonReader` span rewrite (landed as `JsonlLineParser`); current read-path behavior is locked by the #186 tripwires (`Parse_UserLine_StaysBounded`, `GetMessages_SeededStore_StaysBounded`) — re-measure before quoting.

### Renderer-moat probes (`RendererMoatPerfTests`, 2026-08-31, Release)

Live probes over a real 120×500 chat timeline (virtualized feed, spinner tick, ~2× viewport of content) — not synthetic grids. Run:
```bash
dotnet exec tests/Harbor.Tui.CellForge.Tests/bin/Release/net10.0/Harbor.Tui.CellForge.Tests.dll \
  --treenode-filter "/*/*/RendererMoatPerfTests/*"
```

| Probe | Before (pre-sprint full scan) | After (T1 partial scan + T3 effects) | Budget |
|---|--:|--:|--:|
| Diff time, full scan, 120×500 | 0.65–0.72 ms | 0.717 ms | < 2 ms ✅ |
| Diff time, hinted steady frame, 120×500 | — (always full) | **0.317 ms** (T1: 0.347 ms) | < 2 ms ✅ |
| Frame time (solve+paint+hinted diff+encode) | 0.433 ms (T1) | **0.805 ms**¹ | 16 ms ✅ |
| Frame time with armed post-fx glow (status row) | — | **0.671 ms**² | 16 ms ✅ |
| Steady-frame allocations, 2 000 hinted frames | 0 B (T1) | **0 B** (armed-empty pipeline) | 0 B ✅ |

¹ Frame time varies with feed width/machine load across probe runs (0.43→0.81 ms between sprints); the hard acceptance is the diff budget (< 2 ms), which holds with 6× headroom.
² Armed pipeline over an animated glow region measures within noise of the disarmed path — the armed-empty steady state stays byte-identical and allocation-free (test-enforced).

Machine: Linux x64, .NET 10 Release JIT, no tty I/O (discarding backend).

### One RENDER invalidation, and what a burst of them costs (#396)

**The unit of currency for any coalescing decision on the frame loop is one repaint
invalidation, and it was already measured above** — so nothing here needs a new
timer. From the two tables above:

| Cost of ONE repaint invalidation | Value | Source |
|---|--:|---|
| Hinted diff only, 120×500 live chat timeline | **0.317 ms** | `RendererMoatPerfTests` |
| Full frame (solve + paint + hinted diff + encode), 120×500 | **0.805 ms** | `RendererMoatPerfTests` |
| Token frame (~300 changed cells), 200×50 grid | **52.8 µs** | `DiffEngineBenchmark` |
| Full repaint, 200×50 / 400×120 grid | 137 µs / 665 µs | `DiffEngineBenchmark` |

Budget is `< 16 ms/frame` (`specs/07-tui.md`), so a repaint costs **0.3–0.8 % of the
frame budget** on a real feed. Multiply by the burst depth and the fire is obvious:
a token stream emitting 1000 deltas/s at one repaint each would ask for
**~800 ms of frame time per second** — the UI stops being a UI. That multiplication is
the entire justification for coalescing, and it is why the number above is the one to
design against rather than intuition.

**Measured verdict for #396: the coalescing is already in place, and the fire is
already out.** The frame loop keeps two channels with deliberately different semantics
(`ReplLifecycle.LoopAsync`):

| Channel | Type | Semantics | Invalidation count for a burst of N deltas |
|---|---|---|---|
| `_events` | `Channel<AgentEvent>` | lossless, arrival order, drained to empty **before** the frame renders | N domain events, never coalesced |
| `_wake` | `Channel<object?>` | level-triggered — every write is the literal `null`, and `DrainWake` discards the token contents | **1 repaint** |

Four independent levels collapse a burst, which is why the answer is "already done"
rather than "already partially done":

1. `DrainWake()` — N wake writes collapse to one frame (`ReplLifecycle.cs`).
2. `_events` is drained by `while (TryRead)` in one pass, then the frame renders once.
3. `RenderFrameGatedAsync` — a wake whose model version is unchanged produces **zero**
   terminal writes; `FrameTicker` paces to 60 fps and defers, never drops.
4. `StreamCoalescer` — the paced reveal: N deltas mark damage once per tick, and
   `CommitTickPacer` reveals at most one line per tick (Smooth) or drains the backlog
   (CatchUp).

So there is no queue of render invalidations to put a priority lane on, and adding one
would be a new axis for no measured gain. The #555 freeze that forbade it was
lifted on 2026-10-20, so an axis is additive rather than forbidden — but a
priority lane nobody measured is not worth declaring in `PluginAxisRegistry`,
let alone wiring. An approval, a
cancellation or a permission request is in `_events`; `_events` is drained in the same
loop iteration that paints the burst, and `ChatScreenBridge.Tick` drains the gate queue
before the paced reveal — the ordering inside `Tick` is the lane, and it is now pinned
by `ApprovalRequestedBehindABurst_LandsOnTheSameTick_NotAfterItDrains`.

**The one asymmetry, measured and left alone.** `StreamCoalescer.IncomingThinking`
marks damage per thinking delta (`MarkDirty(_thinkStream)`), while the text path marks
once per tick from `DrainPaced`. It is not a fire: `MarkDirty` folds into
`_pendingDirtyFrom` with a `Math.Min` and `MarkHeightsDirty(lastIndex)` is O(1) for the
tail block, so the marks are idempotent and the frame still paints once. It is recorded
here rather than fixed because the fix would be cosmetic — the missing piece is a
per-delta counter (epic #46/#409), not a code change.

Machine for this section: same as the two tables above (Linux x64, .NET 10 Release JIT,
no tty I/O). No new timer was run for it — every figure is copied from the rows it
names, per the rule at the top of this file.

### Virtualization honesty (#412, 2026-10-01, Release)

`TimelineLayoutCache.PrepareLayout` over a 10 000-block transcript, each block 12
logical lines × 100 chars, in a 40-row viewport. The block is a
`WrappingBlock`: its height depends on its width and its `CheapEstimate`
deliberately disagrees with its `Measure`, so a stale layout cannot pass for a
correct one.

These are **counters, not wall-clock** — the point of the slice. Before #412 a
width change called `CheapEstimate` once per block, so a resize scanned the whole
transcript to lay out ~40 visible rows.

| Pass | Before | After | Bound |
|---|--:|--:|---|
| Cold layout, 10 000 blocks, width A — `Measure` | 4 | 4 | ≤ visible window (4 blocks) ✅ |
| Cold layout — `CheapEstimate` | 10 000 | 10 000 | == Count: unavoidable at first layout, documented ✅ |
| Characters scanned on that cold layout | 12 120 000 | 12 120 000 | O(transcript), once ✅ |
| Scroll frame through measured heights (worst of 2 000) | 0 est / 2 meas | 0 est / 2 meas | est == 0, meas ≤ viewportH ✅ |
| Flip to unseen width B — `CheapEstimate` | 10 000 | 10 000 | == Count, ring miss ✅ |
| **Return to measured width A** — `CheapEstimate` | **10 000** | **0** | == 0 ✅ |
| **Return to measured width A** — `Measure` | 4 | 0 | == 0 ✅ |
| 50 width flips — `CheapEstimate` total | 500 000 | 20 000 | ring-bound ✅ |
| 50 width flips — wall clock | 1 319–1 664 ms | 58–101 ms | ~16× ✅ |
| Retained widths after 190 distinct widths | n/a (no cache) | 3 | ≤ 3 (active + 2) ✅ |

The per-item ratio the issue asked for: **10 000 blocks, ~4 in the visible
window, 2500× more items charged than the user sees** on a width change — now
**0×** when the width has been laid out before.

Two honest caveats, both in the XML docs on `PrepareLayout`:

- An **unseen** width still costs `CheapEstimate == Count`. Sustained resize-drag
  (a fresh width every frame) stays on that path, bounded by the 2-width ring.
  Width-keying makes the round trip free; it does not make an arbitrary new
  width cheap, and the doc says so.
- A rejected alternative is recorded on `Slot`: seeding the new width by scaling
  the outgoing row counts. Rows do **not** scale as `fromWidth/toWidth` — word
  breaks, collapse budgets and height-invariant blocks (images) all break that
  ratio. Against a cold-cache oracle a 200→50 flip reported `TotalHeight` 10 288
  where the truth was 4 998, moving `EntryAtY` and the scrollbar extent with it.
  Cheap was not worth wrong.

Run:
```bash
dotnet exec tests/Harbor.Tui.CellForge.Tests/bin/Release/net10.0/Harbor.Tui.CellForge.Tests.dll \
  --treenode-filter "/*/*/TimelineLayoutCacheTests/*"
```

Machine: Linux x64, .NET 10 Release JIT, in-process (no tty, no render loop).

## 6. Test suite

### 6.1 Per-project results — CI, Release ✅ measured

**Source: CI run [39434075758](https://github.com/refusedguy/Harbor-Harness/actions/runs/39434075758)**
(push to `dev`, sha `07769e7e`, 2026-10-20 13:51 UTC), the four `test` shards of
`.github/workflows/ci.yml` on `ubuntu-24.04`, .NET SDK 10.0.402, Release binaries
built by the same run's `build` job.

This is the first `dev` push whose shards ran on the current tree. The `dev`
pushes before it (2026-10-20 09:17 to 13:25) failed in the `build` job, on an
analyzer diagnostic (`Proj0040`, then `Proj0037` and a duplicate local in
`tests/Harbor.Tools.Builtin.Tests/McpOAuthTests.cs`) that the
`dotnet-project-file-analyzers` package upgrade brought in; `07769e7e` fixed
those three, and this is the run after the fix.

Every number below is **MEASURED**, and none of it is a projection:

- **Discovered** is the runner's own `total:` from its "Test run summary" line,
  which counts the tests the runner DISCOVERED. It is not the passed count: the
  `Passed` column is separate, and the gap between the two is the skipped
  tests, not a missing measurement.
- **Duration** is the runner's per-assembly duration.
- The shard wall is the job step's own wall (`Run shard <shard>`).

| Test project | shard | Discovered | Passed | Failed | Skipped | Duration |
|---|---|---:|---:|---:|---:|---:|
| `Harbor.Architecture.Tests` | core | 1 032 | 1 032 | 0 | 0 | 78.75 s |
| `Harbor.Application.Tests` | core | 578 | 578 | 0 | 0 | 16.09 s |
| `Harbor.Abstractions.Tests` | core | 257 | 257 | 0 | 0 | 18.24 s |
| `Harbor.Core.Tests` | core | 239 | 238 | 0 | 1 | 6.40 s |
| `Harbor.ChangeTracking.Tests` | core | 126 | 126 | 0 | 0 | 4.64 s |
| `Harbor.Hosting.Tests` | core | 112 | 112 | 0 | 0 | 0.80 s |
| `Harbor.Config.Tests` | core | 105 | 105 | 0 | 0 | 0.50 s |
| `Harbor.Registries.Tests` | core | 104 | 104 | 0 | 0 | 1.46 s |
| `Harbor.TestKit.Tests` | core | 55 | 55 | 0 | 0 | 0.44 s |
| `Harbor.Domain.Tests` | core | 51 | 51 | 0 | 0 | 3.03 s |
| `Harbor.Lsp.Tests` | core | 35 | 35 | 0 | 0 | 1.05 s |
| `Harbor.Telemetry.Tests` | core | 25 | 25 | 0 | 0 | 0.53 s |
| `Harbor.Terminal.Pty.Tests` | core | 14 | 14 | 0 | 0 | 0.44 s |
| `Harbor.TestKit.Generators.Tests` | core | 4 | 4 | 0 | 0 | 1.06 s |
| `Harbor.Tui.CellForge.Tests` | ui | 2 425 | 2 425 | 0 | 0 | 8.05 s |
| `Harbor.Ui.Framework.Tests` | ui | 627 | 627 | 0 | 0 | 23.94 s |
| `Harbor.App.Avalonia.Tests` | ui | 356 | 349 | 0 | 7 | 2.25 s |
| `Harbor.Tui.Tests` | ui | 244 | 244 | 0 | 0 | 0.74 s |
| `Harbor.Tui.CellForge.PtyTests` | ui | 61 | 61 | 0 | 0 | 35.42 s |
| `Harbor.Terminal.Abstractions.Tests` | ui | 76 | 76 | 0 | 0 | 0.87 s |
| `Harbor.DesignSystem.Tests` | ui | 53 | 53 | 0 | 0 | 0.82 s |
| `Harbor.Tui.RendererTests` | ui | 37 | 37 | 0 | 0 | 0.75 s |
| `Harbor.Tools.Builtin.Tests` | platform | 469 | 467 | 0 | 2 | 5.16 s |
| `Harbor.Storage.Tests` | platform | 147 | 147 | 0 | 0 | 3.29 s |
| `Harbor.Providers.Tests` | platform | 144 | 144 | 0 | 0 | 3.68 s |
| `Harbor.Storage.Jsonl.Tests` | platform | 130 | 130 | 0 | 0 | 7.49 s |
| `Harbor.Plugins.Runtime.Tests` | platform | 105 | 105 | 0 | 0 | 3.84 s |
| `Harbor.Ipc.Tests` | platform | 132 | 128 | 0 | 4 | 4.48 s |
| `Harbor.Transport.Remote.Tests` | platform | 17 | 17 | 0 | 0 | 0.54 s |
| `Harbor.Plugins.Abstractions.Tests` | platform | 17 | 17 | 0 | 0 | 0.40 s |
| `Harbor.Plugins.Host.Tests` | platform | 14 | 14 | 0 | 0 | 0.43 s |
| `Harbor.App.Cli.Tests` | e2e | 361 | 361 | 0 | 0 | 1.48 s |
| `Harbor.E2E.App.Avalonia` | e2e | 107 | 86 | 1 | 20 | 25.86 s |
| `Harbor.Tui.E2E.Tests` | e2e | 30 | 29 | 0 | 1 | 0.54 s |
| `Harbor.E2E.Framework.Tests` | e2e | 26 | 26 | 0 | 0 | 0.52 s |
| `Harbor.LoadTests` | e2e | 16 | 16 | 0 | 0 | 1.24 s |
| `Harbor.E2E.Cli` | e2e | 11 | 11 | 0 | 0 | 3.72 s |
| **Total (37 projects, 4 shards)** | | **8 342** | **8 306** | **1** | **35** | **268.9 s** |

**Shard walls, same run** (`Run shard <shard>` step): `core` **140 s**, `ui`
**78 s**, `platform` **36 s**, `e2e` **37 s**. The PR wall is the maximum
shard, not the sum, so `core` is the binding shard on this run.

**One test failed in the baseline run**: `Harbor.E2E.App.Avalonia`'s
`Chat_ShowToolCallCard` ("expected rendered 'read', but the visible tree never
showed it"), which is why the `e2e` shard is red on it and that run's
conclusion is `failure`. It does not change any count above (the test was
discovered and it failed, which is what the Failed column says), and it is why
the run is quoted as a measurement rather than as a green one. The same test
passed on run 39438762137, so the flake is one flake and not a broken suite.

**How to reproduce the table.** The counts and the per-assembly durations are in
the run's job log (the "Test run summary" block per project). They are also in
the TRX files, and `tools/test-census.py` turns those into exactly this table,
per project and per test:

```bash
gh run download 39438762137 --name test-results-core --dir TestResults/core
python3 tools/test-census.py TestResults/core          # the table above
python3 tools/test-census.py TestResults/core --format csv > census.csv
```

The census reads every result the runner produced, including the ~99% of tests
that printed nothing, so its `discovered` column is the number of tests the
suite has, and its `wall` column is the runner's per-assembly duration read
from the TRX `<Times>` window. On run 39438762137 the census and the job log
agree to the test: 8 342 discovered, 8 307 passed, 35 skipped, 0 failed, in the
same 37 projects.
`tools/test-measurements.py` cannot do this: it renders only the tests that
printed a measurement, which on a full run is 88 rows out of 8 342.

> **The TRX artifacts came back in this PR.** They were gone from every run
> between 2026-10-20 11:17 and this one. The TUnit 1.75 upgrade (`b6fcc333`)
> changed how the runner resolves a relative `--results-directory`: the TRX
> files landed under the project's own output directory
> (`tests/<project>/bin/Release/net10.0/TestResults/<shard>/`) instead of the
> workspace root, so neither the artifact upload (`TestResults/**`, relative
> to the workspace) nor `tools/test-measurements.py` could see them, and every
> run after the upgrade produced no `test-results-*` artifact at all. The
> results directory is now absolute, the four artifacts are uploaded again
> (588 KiB core, 824 KiB ui, 256 KiB platform, 115 KiB e2e on run 39438762137),
> and the census runs in the job summary of every shard.

> **Superseded: the 2026-07-18 local row.** The table this section replaces said
> 45 tests / 2.19 s for `Harbor.Architecture.Tests` and "~493" for the suite.
> That was true of the tree of that day (a local Debug, no-build run on
> linux-x64, .NET 10.0.302, before the #186 tripwires). It is kept here as a
> dated record and it is NOT a baseline:
>
> | Test project | Passed | Failed | Skipped | Duration |
> |---|---:|---:|---:|---:|
> | `Harbor.Abstractions.Tests` | 34 | 0 | 0 | 404 ms |
> | `Harbor.Core.Tests` | 51 | 0 | 1 | 694 ms |
> | `Harbor.Tui.Tests` | 220 | 0 | 0 | 1.44 s |
> | `Harbor.Tools.Builtin.Tests` | 69 | 0 | 1 | 1.21 s |
> | `Harbor.Plugins.Runtime.Tests` | 23 | 0 | 0 | 3.36 s |
> | `Harbor.Scripting.Tests` | 51 | 0 | 0 | 897 ms |
> | `Harbor.Architecture.Tests` | 45 | 0 | 0 | 2.19 s |
> | `Harbor.Config.Tests` | (all pass) | 0 | 0 | ~500 ms |
> | `Harbor.Providers.Tests` | (all pass) | 0 | 0 | ~500 ms |
> | `Harbor.Storage.Jsonl.Tests` | (all pass) | 0 | 0 | ~500 ms |
> | `Harbor.Storage.Tests` | (all pass) | 0 | 0 | ~500 ms |
> | `Harbor.Tui.E2E.Tests` | (requires terminal) | — | — | — |
> | **Total (measured)** | **~493** | **0** | **2** | **~12 s** |
>
> The suite grew from ~493 to 8 342 discovered tests (17x) and the architecture
> suite from 45 to 1 032 (23x) between that measurement and this one. A number
> measured on a tree that no longer exists cannot be a baseline: a spike has
> nothing to compare against, and a diff against 45 says nothing about 1 032.

**The architecture suite, measured four times** (the number the ArchUnitNET
spike asks for). Same shard, same Release binaries, different trees and
different runner loads:

| Run | Tree | Discovered | Passed | Duration | Source `[Test]` attributes |
|---|---|---:|---:|---:|---:|
| [39389070999](https://github.com/refusedguy/Harbor-Harness/actions/runs/39389070999) (PR branch, `3d2f76ae`, 07:49) | before #1868 | 966 | 966 | 65.88 s | 981 |
| [39399653820](https://github.com/refusedguy/Harbor-Harness/actions/runs/39399653820) (dev, `2435d26d`, 09:17) | before #1791 | 977 | 977 | 95.47 s | 992 |
| [39421864352](https://github.com/refusedguy/Harbor-Harness/actions/runs/39421864352) (dev, `86b52adc`, 12:23) | with #1868 | 998 | 998 | 122.10 s | 1 013 |
| [39423289101](https://github.com/refusedguy/Harbor-Harness/actions/runs/39423289101) (PR #1939 branch, `27114126`, 12:34) | with #1789 | 1 002 | 1 002 | 119.20 s | 1 018 |
| [39434075758](https://github.com/refusedguy/Harbor-Harness/actions/runs/39434075758) (dev, `07769e7e`, 13:51) | the baseline row of §6.1 | **1 032** | **1 032** | **78.75 s** | 1 053 |
| [39450959681](https://github.com/refusedguy/Harbor-Harness/actions/runs/39450959681) (PR branch, `f2491032`, 15:5x) | after #1791's shared tree snapshot | 1 068 | 1 068 | 89.43 s | 1 089 |

The last column is the source-level `[Test]` count in
`tests/Harbor.Architecture.Tests/`, which runs 15-21 above the discovered
count on every one of those trees (the difference is `[Test]` occurrences that
the runner does not discover). It is a check on the measurement, not a
substitute for it.

**Read the count, not the wall.** The runs above discovered 966, 977, 998,
1 002, 1 032 and 1 068 tests, and the wall ranged from 65.88 s to 122.10 s for
nearly the same suite. The wall moved by 2x between runs on trees that differ
by 4 % in test count, because a shared runner's load moves it and because the
architecture suite's wall is set by a handful of long tests (§ below). The
discovered count is the stable number; the wall is a range, and a spike should
be compared against the count first and the wall second.

The last row is also the newest tree, and it shows what a code change does to
the wall without touching the count much: #1791's shared tree snapshot (one
compilation for the rules that read the same tree) landed between the two, and
the four `PanelKeyRouteReachabilityRule` tests fell from 112 s to 73 s. The
suite grew by 36 tests and got faster.

**What the architecture suite's wall is made of** (run 39438762137, read with
`tools/test-census.py`): 1 032 tests, a sum of per-test durations of **1 888.72
s** and a wall of **132.60 s**, a parallelism factor of **14.24**. So the suite
is not one serial chain: it runs about fourteen lanes at once. The wall is set
by the slowest single test, not by the total: the four
`PanelKeyRouteReachabilityRule` tests take 112 s each, the pilot Roslyn analyzer
test 108.0 s, and the `ProcessEnvIsolationRule` family 96-99 s each. The same
numbers on the older tree of run 39399653820 (977 tests): 1 108.01 s of test
time in a 95.39 s window, a parallelism factor of 11.62, and a floor set by
79 s tests. The shape is the same on both; the tree grew by 5.5 %.

**The closest thing to the spike's own question, already measured.**
`PilotRoslynAnalyzerTests` runs a Roslyn analyzer over the live tree, which is
what an ArchUnitNET-style spike would do. On run 39438762137 it printed:

```
[pilot] HARCH001+HARCH002 over 61 product compilations: 76885 ms
```

61 product compilations, 76.9 s of analyzer work, on the Release build CI
actually gates on (the older tree of run 39399653820 said 62 compilations and
69.9 s). That is the current cost of the existing analyzer-based rule, and it
is the number a spike should be compared against.

**What a spike can compare against now.** Before this section, the only
numbers in this file about the architecture suite were 45 tests and 2.19 s,
measured on a local Debug, no-build run of a tree 23x smaller, in a
configuration CI never runs. That is replaced by five Release measurements of
the configuration the gate actually runs (1 032 discovered / 78.75 s on the
current baseline run), the shape of that wall (1 888.72 s of test time inside a
132.60 s window, a parallelism factor of 14.24, a floor set by four 112 s
tests), and the cost of the closest existing thing to the spike (a Roslyn
analyzer over 61 product compilations, 76.89 s). A spike can price itself
against those. The Debug half of the axis and the "dependencies discovered"
half are still open, and §6.5 says so instead of leaving them implied.

### 6.1.1 The shard balance: the architecture suite gets a shard of its own (#1781) ✅ measured in CI

**The problem, measured on the current tree.** The `test` job's shard matrix
was laid out by MODULE, and the pull request waits for the MAXIMUM shard
(#1929 measured that the wall is `max(shards)`, not their sum), so the layout
decided the wall. Three consecutive runs on `dev` (the arm64 pool, the pool
issue #1952 moved the shards to), read from the `Run shard <shard>` step of
each job:

| run | tree | `core` | `ui` | `platform` | `e2e` | max/min |
|---|---|---:|---:|---:|---:|---:|
| [39504714459](https://github.com/refusedguy/Harbor-Harness/actions/runs/39504714459) | `b9f3c83c`, 2026-10-20 | **143 s** | 62 s | 35 s | 24 s | **5.96×** |
| [39500486942](https://github.com/refusedguy/Harbor-Harness/actions/runs/39500486942) | `d56aef9f`, 2026-10-20 | **144 s** | 55 s | 35 s | 30 s | 4.80× |
| [39500372206](https://github.com/refusedguy/Harbor-Harness/actions/runs/39500372206) | `90bba174`, 2026-10-20 | **147 s** | 62 s | 34 s | 26 s | 5.65× |

The binding shard is `core`, and it is one project: of its 143 s,
`Harbor.Architecture.Tests` is 91 s, and the thirteen projects next to it cost
36.8 s together. The per-project durations of the first run, read from the
runner's own "Test run summary" block:

| project | `core` shard | reported | project | other shard | reported |
|---|---|---:|---|---|---:|
| `Harbor.Architecture.Tests` | core | **91.01 s** | `Harbor.Tui.CellForge.PtyTests` | ui | 34.84 s |
| `Harbor.Core.Tests` | core | 7.27 s | `Harbor.Tui.CellForge.Tests` | ui | 7.06 s |
| `Harbor.Application.Tests` | core | 7.23 s | `Harbor.Ui.Framework.Tests` | ui | 5.75 s |
| `Harbor.ChangeTracking.Tests` | core | 5.39 s | `Harbor.Ipc.Tests` | platform | 5.09 s |
| `Harbor.Abstractions.Tests` | core | 4.54 s | `Harbor.Plugins.Runtime.Tests` | platform | 4.93 s |
| `Harbor.Domain.Tests` | core | 2.48 s | `Harbor.Storage.Jsonl.Tests` | platform | 4.87 s |
| the other eight | core | 9.84 s | `Harbor.E2E.App.Avalonia` | e2e | 14.70 s |

**What changed.** `Harbor.Architecture.Tests` got a shard of its own (`arch`),
and the other thirteen projects stayed in `core`. The suite is NOT cut into
pieces: it already runs about fourteen lanes at once (§6.1, the parallelism
factor of 14.24), its wall is set by its slowest single test, and splitting
one project across jobs is a different change with a different risk. The
project is also not referenced by any other test project, so its 1 094 tests
run once and not once per referencing shard.

**What that buys, computed from the measured per-project costs** (the table
above, plus the overhead the shard spends on the projects rather than in them:
0.6 s per project, measured at 0.56-0.66 s in all four shards, and 2.0 s per
shard for the first project's JIT warm-up, measured at 0.6-6.5 s). The costs
live in [`tools/shard-costs.json`](../tools/shard-costs.json), which is the
input of the gate below:

| shard | projects | measured | wall before | wall after |
|---|---:|---:|---:|---:|
| `arch` | 1 | 91.0 s | (inside `core`) | **93.6 s** |
| `core` | 13 | 36.8 s | 138.2 s | 46.6 s |
| `ui` | 8 | 53.6 s | 60.4 s | 60.4 s |
| `platform` | 9 | 28.6 s | 36.0 s | 36.0 s |
| `e2e` | 6 | 21.4 s | 27.0 s | 27.0 s |

The heaviest shard falls from 138.2 s to 93.6 s on the model's own walls
(143 s measured), and the skew from 5.11× to 3.46× (5.96× measured). The
wall of the heaviest shard is now the architecture suite's own wall, which is
a cost with an owner (the tree scan and the analyzer compile behind it)
rather than a layout accident.

**What the change measured, on the two runs of the pull request that carries
it** (the `Run shard <shard>` step, the same one the table above reads). The
before column is the dev push of run 39504714459; the two after columns are
the same tree measured twice, and a runner's own speed moves a shard, so
they are two samples of one number and not a floor:

| shard | projects | wall before | wall after (run 39513678361) | wall after (run 39515982740) |
|---|---:|---:|---:|---:|
| `arch` | 1 | (inside `core`) | **97 s** | **94 s** |
| `core` | 13 | 143 s | 45 s | 44 s |
| `ui` | 8 | 62 s | 56 s | 66 s |
| `platform` | 9 | 35 s | 31 s | 30 s |
| `e2e` | 6 | 24 s | 32 s | 26 s |
| **heaviest** | | **143 s** | **97 s** | **94 s** |
| **max/min** | | **5.96×** | 3.13× | 3.62× |

Three readings in that table are worth naming rather than smoothing over.

*The model under-predicts the architecture shard by 0.5-3.4 s.* The suite
reported 96.83 s in its own shard on the first run and 93.11 s on the second,
against 91.01 s inside `core` (1 094 tests in all three). The difference is
not the layout: the suite is now the FIRST project of its shard, so it pays
the JIT warm-up that the 2.0 s per-shard constant only partly covers. The
measured 94-97 s, not the computed 93.6 s, is what the run cost.

*The `e2e` shard moved by 6 s between the two runs, and it is the runner,
not the layout.* Its headless Avalonia project's critical path was 20.1 s on
the first run and 15.2 s on the second (the same 107 tests, the same three
processes, `tests/Harbor.E2E.App.Avalonia/e2e-shards.json`).

*The `ui` shard moved by 10 s between the two runs* for the same reason: the
runner's own speed, not the layout. The layout is what the gate judges, and
the gate judges it by a recorded measurement on purpose.

**What the gate is, and why it is not a timing gate.** `tools/check-shard-balance.py`
reads the shard lists out of their one source (`tools/ci-project-lists.json`,
issue #1800) and the recorded cost of every project
out of `tools/shard-costs.json`, computes what each shard would cost, and
fails when the heaviest and the lightest are more than 4× apart. It measures
NO time: the house policy says a gate that measures time is a benchmark,
because a threshold on a live wall clock reddens on a slow runner (a runner's
own speed moved a shard by 2× between the runs above) and a gate that reddens
on a slow runner gets switched off. What the gate judges is the LAYOUT, by a
recorded measurement, and the recorded measurement carries the run id, the
commit and the date it came from. Its self-test plants a runaway shard, a
project in two shards and a stale cost, and asserts that each of them fails
the gate.

### 6.2 Test execution

> `dotnet test` is not used in this repo — run test projects as plain
> executables, one project at a time, which is what every CI job does. Never
> whole-solution. (An earlier version of this note said `dotnet test` "discovers
> ZERO tests … the host exits 5 with a silent discovery error"; that was wrong —
> exit 5 is MTP's *invalid command-line arguments* code, and a real
> zero-discovery run exits 8. See
> [CONTRIBUTING.md §Why not `dotnet test`](../CONTRIBUTING.md#why-not-dotnet-test).)

```bash
# Build first, then run a single project (Release, no-build)
dotnet build -c Release
dotnet run --project tests/Harbor.Core.Tests -c Release --no-build -- --minimum-expected-tests 1

# Filter to one class (TUnit treenode-filter, forwarded after --)
dotnet run --project tests/Harbor.Tui.Tests -c Release --no-build -- --treenode-filter "/*/*/DefaultUiProjectorTests/*"

# Allocation-budget tripwires (#186)
dotnet run --project tests/Harbor.Registries.Tests -c Release --no-build -- --treenode-filter "*/*/*Allocation*"
```

### 6.3 PTY scenario wall time and parallelism factor (#1789) ✅ measured in CI

`tests/Harbor.Tui.CellForge.PtyTests`, the most expensive single test project in the
repository by wall time, and 75 % of the `ui` shard's cost. It was ONE serial chain:
40 classes carried a single `[NotInParallel("pty")]` constraint key, so the project's wall
was the SUM of its test durations and its parallelism factor was 0.99.

**Method.** The numbers are read from the TRX artifact of a CI run, not from a local
run: `tests/Harbor.Tui.CellForge.PtyTests.trx` from the `test (ui)` shard. The wall is
the TRX `<Times>` window (creation to finish), the same window the runner reports as
the assembly's duration, and the parallelism factor is `sum of per-test durations ÷
wall`. The budget is a CONSTANT (a wall-time ceiling for the project), not a ratio: a
ratio moves when the scenarios get faster, and a ceiling does not.

| Run | Project | Tests | Sum of durations | Wall | Parallelism factor |
|---|---|---:|---:|---:|---:|
| [39399653820](https://github.com/refusedguy/Harbor-Harness/actions/runs/39399653820) (dev, 2026-10-20, BEFORE) | `Harbor.Tui.CellForge.PtyTests` | 81 | 103.58 s | 104.66 s | 0.99 |
| [39416057635](https://github.com/refusedguy/Harbor-Harness/actions/runs/39416057635) (PR #1939, AFTER run 1) | `Harbor.Tui.CellForge.PtyTests` | 81 | ≈64.7 s (est.) | **30.7 s** | ≈2.1 (est.) |
| [39416057635](https://github.com/refusedguy/Harbor-Harness/actions/runs/39416057635) (PR #1939, AFTER run 2, the same job re-run) | `Harbor.Tui.CellForge.PtyTests` | 81 | ≈64.7 s (est.) | **38.0 s** | ≈1.7 (est.) |

**The `ui` shard, measured the same way in all three runs** (the `test (ui)` job
log's per-project timestamps, which are the numbers the workflow puts into its
job summary table):

| run | `ui` shard | of which the PTY project | the PTY share |
|---|---:|---:|---:|
| before | 129.0 s | 105.0 s | 81 % |
| after, run 1 | 73.0 s | 31.0 s | 42 % |
| after, run 2 | 67.0 s | 38.0 s | 57 % |

The project is 3.4× (run 1) and 2.8× (run 2) faster, and the shard is 43 % and
48 % cheaper. The two after runs differ by 7 s, which is the runner's load; the
budget below is set against the slower one.

**Method, and a gap in it.** The before row is read from the TRX artifact (the
sum of the per-test durations divided by the TRX `<Times>` window). The after
rows are read from the job log, because of a CI fact: **the current `ui` shard
no longer produces TRX files** ("No files were found with the provided path:
TestResults/**/*.trx", in both after runs), so the after run's per-test
histogram is not downloadable. The before run's TRX is in the artifact of run
39399653820. The after runs' per-test sum is therefore an ESTIMATE, derived
from the three fixed waits (15.0 s + 16.0 s + 7.9 s off 103.58 s), and the
factor follows from it; the wall is measured either way. The log timestamps
also include the process start and the TRX write, so they are an over-estimate
of the project's own time, which is the conservative direction for a
before/after comparison.

**Budget.** The project's wall must fit in **45 s** per CI run. Before #1789 it was
104.66 s. The ceiling is a constant because the work per run does not change. The
ceiling is deliberately loose: it is there to catch a return to the serial chain
(≈105 s), not to police a few seconds of runner noise. Both after runs fit it,
with 7 s and 15 s of headroom.

**What #1789 removed, in order of what it was worth.** The suite's cost was not
one thing, and the honest order is:

| what | s | note |
|---|---:|---|
| the serial chain itself (one `"pty"` key on 40 classes) | 99.3 % busy | the chain never idled: concurrency level 1 for 103.29 s of the 104.03 s span. There was no idle to reclaim; the key WAS the cost |
| a dead raw-byte wait (`MouseScenarioTests`) | 15.0 | `Session.WaitForOutputAsync(t => t.Contains("WHEEL-OK"), 15 s)` on a cursor-positioned stream: the literal never appears contiguously, so the wait always burned its full timeout and asserted nothing. The TRX of four runs carries "WARN: WHEEL-OK not in raw" every time, with the mock's request count at 5, i.e. the turn had happened and the needle still missed |
| the mock's default 50 ms chunk cadence, in two scenarios | 23.9 | `Server.SetChunkDelay` already existed and one scenario already used it: 1 600 chars is 20.0 s at 50 ms and 4.0 s at 10 ms; 792 chars is 9.9 s and 2.0 s |
| the child boot | 21.6 | 48 spawns of `dotnet exec Harbor.App.Cli.dll` at ≈0.45 s. The PTY device itself is free (spawn = 1–26 ms) |

The first three rows are what #1789 changed; the last is what is left, and it is
the floor of the suite: even with every wait free, 48 child boots are ~21.6 s of
work.

**The app's own clock is the one thing that must not be squeezed.** The quit
gesture's 2 000 ms window is measured by the APP, and the test's 2 300 ms sleep
clears it by 300 ms. That is the margin, and it is why the two Ctrl+C classes
keep a narrow `"quit-gesture"` key (held by those two and by nothing else)
instead of running in parallel with each other.

**It was the only suite in its lane that CHOSE to be serial.** The same `ui` shard
run, every project, wall from the TRX `<Times>` window:

| project | tests | sum of durations | wall | parallelism factor |
|---|---:|---:|---:|---:|
| `Harbor.Tui.CellForge.Tests` | 2 409 | 37.86 s | 8.56 s | 4.42 |
| `Harbor.Ui.Framework.Tests` | 627 | 12.78 s | 6.74 s | 1.90 |
| `Harbor.Tui.RendererTests` | 40 | 1.31 s | 0.72 s | 1.81 |
| `Harbor.Tui.Tests` | 244 | 1.18 s | 0.83 s | 1.42 |
| `Harbor.App.Avalonia.Tests` | 355 | 3.29 s | 2.77 s | 1.19 |
| `Harbor.Terminal.Abstractions.Tests` | 76 | 0.71 s | 1.07 s | 0.66 |
| `Harbor.DesignSystem.Tests` | 53 | 0.34 s | 0.95 s | 0.36 |
| **`Harbor.Tui.CellForge.PtyTests`** | **81** | **103.58 s** | **104.66 s** | **0.99** |

The PTY project is not slow because PTY tests are slow: it is slow because one
constraint key admitted one test at a time. 2 409 renderer tests run in 8.56 s in the
same lane, in the same process, on the same runner.

**Where the time is (before).** The 81 tests are 52 PTY scenarios in 39 classes plus
29 in-process tests that ride along in the same executable (26 `StateTestRunnerTests`
from `Harbor.E2E.Framework`, 3 `PtyScrollbackTests`), 0.31 s of the 103.58 s in
total. The scenario time is NOT evenly spread:

| class | tests | class total (s) |
|---|---:|---:|
| `SessionSwitchMidTurnScenarioTests` | 1 | 21.41 |
| `MouseScenarioTests` | 2 | 17.87 |
| `ResizeMidStreamScenarioTests` | 1 | 11.07 |
| `CtrlCGestureScenarioTests` | 2 | 3.71 |
| `ComposerEditingScenarioTests` | 3 | 2.85 |
| `SubmitScenarioTests` | 2 | 2.55 |
| `TerminalModesScenarioTests` | 2 | 2.24 |
| `MarkdownRenderScenarioTests` | 1 | 2.20 |
| `MouseEdgeScenarioTests` | 2 | 2.19 |
| `BackendAliasScenarioTests` | 1 | 2.08 |
| `ResizeGeometryScenarioTests` | 2 | 1.91 |
| `KittyEdgeScenarioTests` | 2 | 1.80 |
| `PasteEdgeScenarioTests` | 2 | 1.76 |
| `WriteResultScenarioTests` | 1 | 1.40 |
| `TreeErrorScenarioTests` | 1 | 1.40 |
| `ToolErrorScenarioTests` | 1 | 1.38 |
| `PatchDiffScenarioTests` | 1 | 1.38 |
| `ReadErrorScenarioTests` | 1 | 1.38 |
| `GlobErrorScenarioTests` | 1 | 1.37 |
| `PatchErrorScenarioTests` | 1 | 1.37 |
| `GrepErrorScenarioTests` | 1 | 1.35 |
| `LsErrorScenarioTests` | 1 | 1.35 |
| `AbortMidToolScenarioTests` | 1 | 1.35 |
| `WriteErrorScenarioTests` | 1 | 1.33 |
| `EditDiffScenarioTests` | 1 | 1.32 |
| `EditErrorScenarioTests` | 1 | 1.31 |
| `PermissionDenyScenarioTests` | 1 | 1.31 |
| `ToolCallStreamingScenarioTests` | 1 | 1.21 |
| `ConfigToggleScenarioTests` | 1 | 1.09 |
| `ExtremeResizeScenarioTests` | 1 | 1.09 |
| `TermiosRestoreScenarioTests` | 1 | 1.00 |
| `PtyHarnessSanityTests` | 4 | 0.92 |
| `Utf8ScenarioTests` | 1 | 0.89 |
| `KittyKeysScenarioTests` | 1 | 0.86 |
| `CtrlCScenarioTests` | 1 | 0.85 |
| `PasteInjectionScenarioTests` | 1 | 0.73 |
| `ResizeScenarioTests` | 1 | 0.66 |
| `CrashRecoveryScenarioTests` | 1 | 0.65 |
| `LaunchScenarioTests` | 1 | 0.65 |
| `StateTestRunnerTests` (in-process, from `Harbor.E2E.Framework`) | 26 | 0.29 |
| `PtyScrollbackTests` (in-process) | 3 | 0.02 |

**The three outliers are the floor, and this change does not touch them.** 48.5 s of
the 103.58 s sits in three tests: `NewSession_WhileRunning_Rejected_ThenFreshSessionWorks`
(21.41 s), `SgrWheelUp_ScrollsTimelineBack_RevealingTopContent` (16.12 s) and
`Resize_MidStream_CompletesWithinNewBounds` (11.07 s). They are slow because the mock
provider streams 4 characters per 50 ms and the scenarios ask for 800-character
answers: ~10 s of that is the provider's own cadence, not the harness's. The remaining
78 tests total ~53 s, one to two seconds each, and are dominated by the child's boot
(`dotnet exec Harbor.App.Cli.dll`). There are no fixed sleeps left to remove: the
screen wait is a repaint-signal wait and the quiescence wait is a stability window
(#1792, #1814).

**What the parallelism factor cannot go below.** The longest single test is 21.41 s
before the cadence fix and ≈5.4 s after it, so no amount of concurrency takes the
project's wall under the longest test. The 2-permit limiter (`PtyScenarioLimit`) is
the budget that trades the two off: ≈65 s of work after the fixed-wait fixes,
over two lanes is ≈32 s of work per lane against a ≈5.4 s floor, and the two
measured walls (30.7 s and 38.0 s) sit exactly there. The limiter is what binds,
not the floor: at 4 permits the same work is ≈16 s of work per lane, and that is
the next step, with its own run's numbers before it is believed.

**This work does NOT shorten the PR wall, and the numbers say so.** The PR wall is
the MAXIMUM shard, not the sum. On the run this table is measured from: `core`
159.43 s, `ui` 126.31 s (of which the PTY project is 104.66 s), `e2e` 60.0 s,
`platform` 31.4 s. The `ui` shard is already ≈33 s BELOW `core`, and it only
becomes the binding shard if the PTY project exceeds ≈113 s. Deleting the whole
project would not move the critical path. What the work buys is the `ui` shard's
own wall, the runner occupancy, the flake blast radius (one serial chain of 81
tests means one flake costs the whole project), and the removal of a serial-chain
liability. The shard rebalancing that is happening in parallel is what moves the
critical path, and this change lowers the floor it is working against.

**Re-measured on the current tree (#1884).** The paragraph above is a dated
record of the BEFORE run; the numbers below are what the same shard costs now.
On the baseline run of §6.1 (dev, `07769e7e`, 2026-10-20 13:51 UTC) the shard
walls are `core` 140 s, `ui` 78 s, `platform` 36 s, `e2e` 37 s, so `ui` sits
62 s BELOW `core`. The PTY project itself is 61 tests / 35.42 s on that tree,
not 81 / 104.66 s, so the `ui` shard's sum of per-project durations fell from
126.88 s to 72.85 s. The conclusion is unchanged and now has more room: `ui`
would have to grow by roughly 100 s before it binds the PR wall.

**What the three groups of tests are, priced.** The 81 tests split into three
kinds, classified from the tree and priced with the same TRX:

| kind | classes | tests | time | share |
|---|---:|---:|---:|---:|
| wire / terminal, where the real PTY is the subject (alt-screen, mouse grab, bracketed paste, kitty CSI-u, SGR mouse, UTF-8 split writes, termios, resize, the 0x03 byte, exit codes) | 18 | 28 | 50.86 s | 49.1 % |
| app-level, asserted on the grid, which needs the whole app (CLI process, agent loop, provider, tools, permission gate, config file) and not only the renderer | 21 | 24 | 52.41 s | 50.6 % |
| in-process: `PtyScrollbackTests` (3) and `StateTestRunnerTests` (26, from `Harbor.E2E.Framework`, which rides along in the same executable through its module initializer) | 2 | 29 | 0.31 s | 0.3 % |

The renderer already has an in-process seam (`CellForgeTuiRenderer(ISyncTerminalBackend)`,
`ScreenSession`, `TestBackend`, `InMemoryTerminalBackend`) and 2 409 tests use
it in 8.56 s. The APP does not: the interactive path reads fd 0 directly
(`TerminalInputStream.Open()`) and owns the terminal through
`ITerminalModeController`, and the interactive app is composed by `Program.cs`.
So the 24 grid-asserting tests are not renderer tests. They assert on the
grid the whole app produced, and moving them in-process needs an in-process
interactive host that does not exist today. That is a design decision for the
owner of the CLI composition root, not a test-suite reshuffle.

**A process split of this project, measured (LPT over the class costs above).** A
constraint key is process-local, so splitting the project into CI jobs by
`--treenode-filter` leaves the classes INSIDE each job serial, so the table below is the
cost of splitting INSTEAD of parallelising in-process:

| processes | shard costs (s) | max shard |
|---:|---|---:|
| 1 (today) | 104.66 | 104.66 |
| 2 | 51.88 / 51.70 | 51.88 |
| 3 | 34.43 / 34.48 / 34.68 | 34.68 |
| 4 | 25.84 / 25.90 / 26.17 / 25.67 | 26.17 |

The in-process parallelism this issue delivers lands in the same place as the 4-process
split (≈26–30 s) and beats the 2- and 3-process splits (51.9 s, 34.7 s), because
those numbers assume the classes inside a process stay serial, which is exactly what
removing the key removes. On a four-core runner a process split ON TOP of the in-process
parallelism buys no wall time (the CPU, not the process boundary, is the binding
resource) and costs one runner job per extra shard. The split becomes the right lever
when the runner has more cores; until then the in-process budget is.

### 6.3.1 The screen wait ends on the child's state, not on the deadline (#2001) ✅ measured in CI

**The lever.** Every PTY scenario waits for a repaint on the emulated grid with a
ten-second diagnostic bound. The bound is NOT the lever: when the child process has
exited and the pump has drained its output, no further repaint CAN arrive, so the
wait is already decided and the rest of the bound is waste. The pump already knows
that state (it is how the pump stops), so the wait now ends on it:
`CellForgePtyScenarioBase.WaitForScreenAsync` raises `PtyChildExitedException`, "the
child process exited and its output is drained: no further repaint can arrive". The
ten seconds stay as the emergency bound for a child that is alive and merely quiet.

**Why the mass-failure cost was bigger than 13 × 10 s.** PR #1970 measured thirteen
scenarios failing at once: the PTY block went from ~100 s to 603 s and the `ui` shard
job from 175 s to 678 s. 13 × 10 s is 130 s of waiting, so the other ~470 s was the
`[ParallelLimiter<PtyScenarioLimit>]` (two permits for the whole assembly) serialising
the failing scenarios while the healthy ones queued behind them. Ending the wait on
the state removes the waiting AND the serialisation it feeds: a failing scenario now
holds its permit for the seconds it needs to fail, not for the whole bound.

**Measured on the `ui` shard (arm64), the same tree with and without the change.** The
"before" row is this branch with the new tests and NO fix; the "after" rows are the
same branch with the fix. All three carry the same 64 tests.

| Run | Tree | PTY project | The `ui` shard | The wait after the child's exit | The scenario's duration |
|---|---|---|---|---|---|
| [39469637522](https://github.com/refusedguy/Harbor-Harness/actions/runs/39469637522) (this branch, BEFORE the fix) | the new tests, no fix | 64 tests, 63 passed, **1 failed**; project **38.92 s** (census: sum 76.53 s, wall 38.86 s, factor 1.97) | 81 s | the full bound: `screen condition not met within 00:00:10 (waited 10.0s)` | **10.64 s** |
| [39480291027](https://github.com/refusedguy/Harbor-Harness/actions/runs/39480291027) (this branch, AFTER the fix) | the new tests + the fix | 64 tests, **64 passed**; project **33.82 s** | 73 s | the state: `the child process exited and its output is drained: no further repaint can arrive` | under 3 s (the scenario's own assertion) |
| [39474702848](https://github.com/refusedguy/Harbor-Harness/actions/runs/39474702848) (this branch, AFTER the fix, the earlier base) | the new tests + the fix | 64 tests, **64 passed**; project **37.36 s** | 73 s | the state, as above | under 3 s |
| [39467269890](https://github.com/refusedguy/Harbor-Harness/actions/runs/39467269890) (dev, the baseline) | dev, without the new tests | 61 tests, 61 passed; project **33.83 s** (census: sum 65.88 s, wall 33.77 s, factor 1.95) | 73 s | no such scenario on that tree | not applicable |

**Read the wait, not the project wall.** The project's wall moved by 1.6 s between the
two branch rows of the FIRST pair (37.36 s against 38.92 s), and the runner's own
spread on the same project is larger than that (33.13 s, 33.83 s and 33.82 s on
three runs), so the wall difference is NOT the measurement. The measurement is the
wait: **10.0 s of pure waiting** in the before row (the full bound, and the only way
that wait could end), and a wait that ends on the state in the after rows. The pump
publishes the terminal state within one pump idle cap (100 ms) of the child's death,
and the scenario asserts that the wait ended in under 3 s. One scenario waiting out
the timeout is 10 s of a lane; thirteen of them is 65 s of a two-lane budget, which
is the shape #1970 measured.

**The number of scenarios that wait out the timeout: 1 → 0.** Before the change, the
scenario that waits for a repaint after the child's exit IS a scenario that waits out
the timeout, because that is the only way its wait could end. After the change it
ends on the state, and the deadline is reached only by a child that is alive and
quiet: the `A_Live_Child_That_Does_Not_Paint_The_Condition_Still_Waits_Out_The_Bound`
scenario pins that (1.62 s for a 1 s bound, and the exact `TimeoutException`, not the
child-exited one).

> **A CI fact that hides the per-test numbers of the "after" rows.** Those rows have
> no census: the TRX did not land in the upload path, because `ci.yml` on `dev`
> passes `--results-directory "$GITHUB_WORKSPACE/$trx_dir"` with an
> already-absolute `trx_dir`. The path comes out doubled, so neither the artifact
> upload nor `tools/test-census.py` finds the file. That is a `dev` regression and
> not this branch's: the "before" row, one `dev` commit earlier, has its census. The
> fix is one token, `--results-directory "$trx_dir"`.

### 6.4 CI test-suite wall: `Harbor.E2E.App.Avalonia` (CI runs 39417846300 and 39421557542, dev and this PR, 2026-10-20)

> **Provenance.** The `test (e2e)` shard of two CI runs on the same tree, one before the change and one after: `dev` at 8be16392 (run 39417846300) and this branch (run 39421557542). Both rows are the same measurement, taken from the shard log: the process wall from the `dotnet exec` group marker to the TUnit summary, and TUnit's own reported duration. The test counts are identical, so the difference is the wall and not a lost test.

| Metric | dev (8be16392, run 39417846300) | this PR (run 39421557542) | Δ |
|---|---:|---:|---:|
| process wall | 46.06 s | **20.72 s** | **−25.34 s (−55 %)** |
| TUnit duration (sum of the test durations) | 45.477 s | **20.387 s** | **−25.09 s (−55 %)** |
| tests | 133 (113 passed, 20 skipped) | 133 (113 passed, 20 skipped) | unchanged |

#### What the removed time was

On the run the change was measured against (CI run 39399653820, dev, TRX) the project's wall was **53.43 s**, and five tests were four-second waits that a PRODUCT timer forced from inside the code under test:

| Test | Duration (run 39399653820) |
|---|---:|
| `SessionListTests.SessionList_Deleted_RemovedFromList` | 4.46 s |
| `ToastTests.Toast_AutoDismiss_After4Seconds` | 4.39 s |
| `AvaloniaUiTests.ToastNotification_DisplaysAndDismisses` | 4.15 s |
| `ToastTests.Toast_Multiple_StackedVertically` | 4.05 s |
| `ToastTests.Toast_LongMessage_WrapsWithinMaxWidth` | 4.04 s |
| **Total** | **20.5 s, 38 % of the project's 53.43 s wall** |

`AvaloniaUiTests.MultipleToasts_StackVertically` waited on the same timer and is not in that table, so the removed time is larger than the 20.5 s those five rows add up to.

The wait was `await Task.Delay(TimeSpan.FromSeconds(4))` in
`apps/Harbor.App.Avalonia/ViewModels/MainViewModel.cs:530`, the toast auto-dismiss.
It is a PRODUCT timer, so no test-side change could shorten it: the delay was in
the code under test. #1945 makes the delay read an injected `TimeProvider` (the
seam `AgentLoop`, `ToolDispatcher`, `RetryPolicy` and `MascotDirector` already use),
so the tests step a manual clock instead of sleeping, and the four-second contract
is still asserted, against the production constant `MainViewModel.ToastAutoDismissDelay`.

The shipped behaviour is unchanged: with `TimeProvider.System` the delay takes the same
`TimerQueueTimer` path `Task.Delay(TimeSpan)` took, so a toast still leaves four seconds
after it was pushed.
### 6.5 Architecture-suite tree scan (#1791) — **CONSTANT BUDGET**, CI merge gate

`tests/Harbor.Architecture.Tests`, in the `test (core)` merge-gate shard.

The architecture suite is the slowest artefact in the test set, and the cost
was never the rules' verdicts — it was the layer under them. The wall is not
throughput-bound: a handful of classes each block on their own whole-tree
work, and the rest of the suite queues behind them.

**Two numbers, and they are not the same number.** The *sum of durations* is
what a rule's tests add up to, and it counts a test's WAITING for a shared
lazy once per waiter — so a class whose four tests share one scan shows four
times the scan in that column. The *span* is the class's wall footprint
(last end − first start), and the span is what a critical path is made of.
Both are in the tables below, and the measurement file
(`tests/Harbor.Architecture.Tests/SourceTreeScanMeasurement.cs`) prints the
phases of the three heaviest rules, measured ALONE, so the work can be told
apart from the queueing.

**Before** — run 39413205038 (`test/1791-before`, the tree without the
change), 2026-10-20:

| class | span | sum of durations | what it actually does |
|---|---:|---:|---|
| `PanelKeyRouteReachabilityRule` | 72.9 s | 502.8 s | a text scan of the product trees, and then — for every hand-off it looks up — a re-split of every file it walks past |
| `ProcessEnvIsolationRule` | 67.9 s | 632.2 s | a text scan of the whole test tree, re-derived once per test — nine times over |
| `PilotRoslynAnalyzerTests` | 66.2 s | 169.6 s | **52 Roslyn COMPILATIONS** (1 236 files parsed) — not a text scan |
| `TokenCellSingleHomeRules` | 28.8 s | 28.9 s | a text scan of the product trees against three regexes — one test, so the class's parallelism is 1.00 by construction |
| `ApprovalGateWaiterRaceRules` | 19.1 s | 38.5 s | a text scan of the whole test tree, twice |

**After** — two runs of the same tree plus the change, 2026-10-20: 39418488276
and 39421786081.

| class | span (before) | span (after) | durations (before) | durations (after) |
|---|---:|---:|---:|---:|
| `PanelKeyRouteReachabilityRule` | 72.9 s | 58.6 / 58.9 s | 502.8 s | 235.2 / 237.4 s |
| `ProcessEnvIsolationRule` | 67.9 s | **1.9 / 1.4 s** | 632.2 s | **11.4 / 7.7 s** |
| `PilotRoslynAnalyzerTests` | 66.2 s | 51.4 / 55.4 s | 169.6 s | 94.2 / 110.4 s |
| `TokenCellSingleHomeRules` | 28.8 s | 42.2 / 39.5 s | 28.9 s | 42.2 / 39.7 s |
| `ApprovalGateWaiterRaceRules` | 19.1 s | 12.6 / 15.1 s | 38.5 s | 25.2 / 30.4 s |
| `SharedSourceTreeSnapshotTests` (new) | — | 41.1 / 44.1 s | — | 41.5 / 44.1 s |

| project | before (1 run) | after (2 runs) |
|---|---:|---:|
| tests | 987 | 993 (+6: the snapshot's own guard) |
| sum of durations | 1 693.1 s | **712.1 / 769.4 s** (−55 to −58 %) |
| project wall | 114.5 s | **70.0 / 72.0 s** (−37 to −39 %) |
| parallelism | 14.8× | 10.2× / 10.7× |

The two "after" runs differ by 8 % of the summed duration and 3 % of the
wall, so the wall figure is a range and not a single number. The class that
moves by an order of magnitude — the process-environment rule, 67.9 s →
1.4–1.9 s — moves by more than the spread between the runs, and it moves for
a reason a reader can check: the rule re-derived the same answer nine times
and now derives it once.

Two caveats, stated rather than buried. The "before" wall includes the
measurement's own serial run at the end of the assembly (27.9 s); the
"after" includes the guard's 41–44 s, which ran CONCURRENTLY with the
classes (inside the wall), so it did not extend it — the guard's own work is
the two comment-strips it does per file, about 2.5 s for the whole project,
and its REPORTED duration is contention rather than work, because it ran
alongside six other CPU-bound classes. And the classes that move UP
(`TokenCellSingleHomeRules` 28.8 → 39.5–42.2 s) move for the same reason
the ones that move down move: neither rule's work changed, and both are
scheduled against a queue whose order shifted when nine test-tree parses
stopped competing for the CPU. The total is what the suite pays.

Two things follow from the phases, measured alone on the "before" tree, and
they are the whole shape of the fix:

* **The reads are not the cost.** Reading 1 236 product files takes **46 ms**,
  and comment-stripping the whole test tree takes **3 556 ms**. The cost is the
  ANALYSIS — the panel/overlay scan is **17 453 ms**, the process-environment
  discovery **3 338 ms**, the approval-gate scan **3 468 ms**. A shared
  snapshot of the file BYTES would not touch that; a shared snapshot of the
  file's LINE VIEW, and a memo over it, do.
* **The repetition is the cost.** The process-environment rule re-derived the
  same answer nine times and the approval-gate rule twice; both are pure
  functions of a tree that does not change while the suite runs.

`MatchesPatternScalingGates.Cost_GrowsLinearly_InRuleCount` (34.0 s) is in
`Harbor.Abstractions.Tests` and is not a tree scan at all: it is a relative
cost gate that measures `PermissionRuleset.Evaluate` over 256 and 512 rules,
and its duration is the gate's own measurement budget.

**The budget is a COUNT, not a duration.** A duration measures the runner; a
count measures the code. The shared snapshot
(`tests/Harbor.Architecture.Tests/SharedSourceTreeSnapshot.cs`) is held to:

| Claim | Gate | Where |
|---|---|---|
| one snapshot per process | exactly 1 instance for the whole assembly, asserted as a test AND from an `[After(HookType.Assembly)]` hook — a test can only see a second snapshot that already existed when it ran | `SharedSourceTreeSnapshotTests.Exactly_One_Snapshot_Exists_For_The_Whole_Process` |
| one read per file | `reads == distinct files read`, for the whole assembly | `..._Reads_Each_File_Once_And_Derives_Each_Line_View_Once` |
| at most two line views per file | `line views ≤ 2 × files` | the same test |
| the bytes the rules used to read | the snapshot's raw text, its `'\n'` line view and its `ReadAllLines` line view, each compared against a fresh read computed the way each rule computed it before | `..._Hands_Out_The_Bytes_The_Rules_Used_To_Read` |
| the file set the rules used to walk | the snapshot's walk, compared against a fresh walk with the same filter | the same test |
| the snapshot still describes the tree | every captured file's size and mtime, and every captured directory's file list, re-checked against the disk | `..._Still_Describes_The_Tree_On_Disk` |
| the checker can fail | a changed file, a deleted file and a grown directory are planted and the checker has to report them; an unchanged tree has to report nothing | `..._Reports_A_Planted_Defect` |

> The `coverage` job re-runs the architecture suite a second time (report-only,
> no gate), so the suite's cost per PR is currently paid twice.

### 6.4 CI lane walls: the build job, the compile step, the artifact transfer ✅ measured

The test shards are half of the CI bill. The `build` job compiles everything once
and hands the binaries to six consumers, so the compile step and the artifact
transfer sit on the PR wall too. Every number in this section is **MEASURED**
from the job API (`started_at` / `completed_at` per step) and from the
artifacts' `size_in_bytes`, on `ubuntu-24.04` with SDK 10.0.402.

**The build job and the compile step** (the NUKE `Compile` target, Release):

| Run | Tree | `build` job wall | NUKE `Compile` step | PR wall (first job start → last job end) |
|---|---|---:|---:|---:|
| [39389070999](https://github.com/refusedguy/Harbor-Harness/actions/runs/39389070999) (07:49) | `3d2f76ae`, PR branch, one artifact | 546 s | 389 s | 2 339 s |
| [39399653820](https://github.com/refusedguy/Harbor-Harness/actions/runs/39399653820) (09:17) | `2435d26d`, dev, one artifact | 562 s | 427 s | 2 142 s |
| [39421864352](https://github.com/refusedguy/Harbor-Harness/actions/runs/39421864352) (12:23) | `86b52adc`, dev, one artifact | 585 s | 434 s | 1 084 s |
| [39424579498](https://github.com/refusedguy/Harbor-Harness/actions/runs/39424579498) (12:44) | `88e9178b`, PR branch, slim lane | 441 s | 295 s | 757 s |
| [39434075758](https://github.com/refusedguy/Harbor-Harness/actions/runs/39434075758) (13:51) | `07769e7e`, dev, slim lane | 576 s | 413 s | 1 045 s |

The slim lane (one artifact per consumer instead of one for all six) landed on
`dev` in `d611d145`, so the last row is the current shape. Its compile step
runs with the architecture gate OFF the PR path; the gate-on path measured
396 s on run 39405042780 (same image, same SDK, one variable).

**The artifact transfer** (the `Download build outputs` step of each consumer):

| Run | Artifact | Bytes | Download |
|---|---|---:|---:|
| 39389070999 | `solution-binaries` (one artifact for six consumers) | 469 943 655 (448.2 MiB) | core 9 s, platform **60 s**, ui 17 s, e2e 10 s, coverage 11 s, perf 10 s |
| 39421864352 | `solution-binaries` | 508 918 319 (485.3 MiB) | core 10 s, platform 16 s, ui 8 s, e2e 16 s, coverage 12 s, perf **36 s** |
| 39434075758 (slim lane, on `dev`) | `solution-binaries-core` | 97 479 368 (93.0 MiB) | 3 s |
| | `solution-binaries-platform` | 63 281 561 (60.3 MiB) | 3 s |
| | `solution-binaries-ui` | 86 179 428 (82.2 MiB) | 3 s |
| | `solution-binaries-e2e` | 82 904 054 (79.1 MiB) | 4 s |
| | `solution-binaries-perf` | 5 745 208 (5.5 MiB) | 2 s (perf-gate) |
| | `solution-binaries` (what `coverage` downloads) | 160 758 552 (153.3 MiB) | 4 s |
| 39424579498 (slim lane, PR branch) | `solution-binaries-platform` | 64 004 763 (61.0 MiB) | 4 s |
| | `solution-binaries-ui` | 86 535 886 (82.5 MiB) | 2 s |
| | `solution-binaries-e2e` | 76 742 782 (73.2 MiB) | 4 s |
| | `solution-binaries-perf` | 5 744 773 (5.5 MiB) | (perf-gate skipped) |
| | `solution-binaries` (what `coverage` downloads) | 161 414 374 (153.9 MiB) | 5 s |

Two things a spike should read off this table. First, the download is not a
constant: on the one-artifact lane the `platform` shard waited 60 s for bytes
it did not need, which is more than the shard's own test wall on some runs. On
the per-consumer lane the same downloads take 2-4 s. Second, the compile
step's own spread across runs (295-434 s here) is larger than the difference
between the lane shapes, so a single run cannot separate "the lane got cheaper"
from "the runner was faster". The range is the honest statement of what these
numbers support.

### 6.5 The same rules in Release and Debug: the axis that is NOT measured yet

The architecture gate runs in **Release** in CI: `Directory.Build.props`
(`HarborArchitectureGate`) executes the whole `Harbor.Architecture.Tests`
assembly after it builds, and the `core` shard runs the same DLL. A baseline
for a spike therefore has to be a Release baseline, which is what §6.1 is. The
Debug half of the axis does not exist, and this section says so instead of
leaving the gap silent.

| Axis | Release (what CI gates on) | Debug |
|---|---:|---|
| Architecture suite, tests discovered | **1 032** (runs 39434075758, 39438762137) | not measured |
| Architecture suite, wall | **78.75 s** (core shard, run 39434075758) | not measured |
| Architecture suite, sum of per-test durations | **1 888.72 s** (run 39438762137, TRX) | not measured |
| Architecture suite, parallelism factor | **14.24** (run 39438762137, TRX) | not measured |
| Analyzer population: product compilations the pilot Roslyn analyzer walked | **61** (run 39438762137) | not measured |
| Analyzer cost over that population | **76.89 s** (run 39438762137) | not measured |

Why the Debug half is empty: no CI job builds Debug (the pipeline is Release
end to end), and the old local Debug row (45 tests / 2.19 s) is the number
this section replaces. One local run fills the cells:

```bash
dotnet build -c Debug tests/Harbor.Architecture.Tests
dotnet exec tests/Harbor.Architecture.Tests/bin/Debug/net10.0/Harbor.Architecture.Tests.dll \
  --minimum-expected-tests 1 --report-trx --results-directory TestResults/debug
python3 tools/test-census.py TestResults/debug
```

The "dependencies discovered" half of the question (the reason the axis is
wanted: an analyzer sees fewer dependencies in a Release build) is **not
measured in either configuration**. The pilot Roslyn analyzer's population (62
product compilations, 69.87 s) is the closest number the repository has, and
it is the one a spike can be priced against today.
### 6.6 CI suite wall — the headless Avalonia E2E project, one process per shard (#1790) **[CI-short]**

> **Provenance.** `test (e2e)` job, `ubuntu-latest` (4 vCPU), GitHub-hosted
> runner. Three runs, each cited by its run id:
>
> * **A — before**, run **39404823168** (dev@03ce3070, TUnit 1.61): the whole
>   project in ONE process.
> * **B — after**, run **39420115031** (this branch, TUnit 1.75): three
>   processes, before the toast-timer change of §6.4 landed. **A → B is this
>   change alone.**
> * **C — final**, run **39456355648** (this branch's head, after the
>   toast-timer change of §6.4, after `StateTestRunnerTests` moved to a
>   project of its own, and after the golden frames started running on CI):
>   three processes, 107 tests instead of 133, and 7 skipped instead of 19.
>   The plan checked in is measured from run **39436580540**.
>
> The runs are on the same runner CLASS but not on the same runner, so read
> the numbers as "the same work in about half the wall", not as a
> machine-independent ratio. §6.4 above measures the SAME project across the
> toast-timer change; the two sections are independent measurements of the same
> wall, and the "before" rows differ because the runs differ.

`tests/Harbor.E2E.App.Avalonia` cannot be parallelised IN-PROCESS: the
driver keeps `Application.Current`, the desktop lifetime and the DI host in
static fields, and Avalonia permits one `Application` per AppDomain. The
driver also sets `$HOME` (not thread-local) and
`CommonConfig.OverrideHarborHomeForTests` pins the Harbor home for the whole
process. All three are process-wide, so the lever is the PROCESS.

| run | processes | critical path | project wall | tests discovered | tests skipped |
|---|--:|--:|--:|--:|--:|
| A, 39404823168 (before) | 1 | 58.59 s (the sum of the class durations) | **60.56 s** | 133 | 20 |
| B, 39420115031 (this change) | 3 | **29.0 s** | **29.6 s** | 133 | 19 |
| C, 39456355648 (final tree) | 3 | **15.1 s** | **15.6 s** | 107 | 7 |

**A → B is the change this section is about:** 60.56 s → 29.6 s, and the
project's wall becomes the slowest shard (29.0 s) instead of the sum of the
class durations (58.59 s). The B run's shards measured 23.7 s (shard-0, 56
tests), 29.0 s (shard-1, 72) and 7.2 s (shard-2, 5) — 29.6 s of wall for
58.59 s of class time, i.e. a parallelism factor of 1.98 over three
processes.

**B → C is not this change:** it is the toast-timer change of §6.4 (which
removed five waits of ~4 s each, ~20.5 s of the project's wall) and
`StateTestRunnerTests` moving to a project of its own (26 tests, 0.2 s).
Run C's shards measured 12.2 s (shard-0, 41 tests), 15.1 s (shard-1, 61) and
4.9 s (shard-2, 5) — the plan is measured from run 39436580540's TRX, so the
next run's split is balanced against ITS costs (11.80 / 11.74 / 1.87 s of
measured class time).

The e2e SHARD as a whole (its projects, sequentially) went from **67.7 s**
(run A) to **38.5 s** (run B) to **24.5 s** (run C): the project above is
60.6 s of the 67.7 s before, and 15.6 s of the 24.5 s after.

**This is not the binding shard.** The `test` job's shard walls on the same
lane: `core` 159.4 s, `ui` 126.3 s, `e2e` 60.0 s, `platform` 31.4 s — so the
PR wall is set by `core`, and this change does not shorten it. What it buys
is the e2e shard's last serial block (a 60 s single point of failure becomes
three independent processes) and one more test that runs: the skipped
`SettingsTests.Settings_Cancel_RevertsChanges` is un-skipped, which is why
the skipped count falls from 20 to 19.

The shard list and its measured costs live in
[`tests/Harbor.E2E.App.Avalonia/e2e-shards.json`](../tests/Harbor.E2E.App.Avalonia/e2e-shards.json);
it is generated by `tools/gen-e2e-shards.py` and graded by
`E2eAvaloniaShardCoverageRules` in
[`tests/Harbor.Architecture.Tests`](../tests/Harbor.Architecture.Tests/).

### 6.7 CI build wall time and core saturation (#1929) — **MEASURED in CI**

Issue #1929 asks whether the CI build is limited by the runner's 4 vCPU or by
the shape of the project dependency graph, and it ships a falsification test:
build the same commit with one compiler at a time and compare the wall time.
Its table reads a ratio near 4 as "the cores are the bottleneck, two jobs
would give eight cores" and a ratio near 1 as "the graph is the bottleneck,
the model is wrong".

**The harness.** `.github/workflows/meas-build-vcpu.yml` runs the CI build
command in four configurations, three replicates each, on the same runner
image. Per run it measures: the wall time, the CPU utilisation sampled from
`/proc/stat` once a second, the CPU accounted per process, MSBuild's own
Performance Summary (`Csc` count and total duration, and the `Exec` task that
runs the architecture gate), and the project dispatch timeline.
`tools/measure-build-cpu.py` runs a command and measures it,
`tools/aggregate-build-vcpu.py` turns the runs into the table below, and
`tools/measure-project-graph.py` parses the dependency graph.

**Provenance.** The same matrix was run five times, on four trees (the branch
before its rebase onto `dev`, after it, after the final rebase, and the
rebase onto the current `dev`): CI runs 39448423292, 39454123726, 39462228165,
39467297561 and 39483960190, 2026-10-20. Fifteen replicates per arm, pooled.
Runner: `ubuntu-latest` for the first four runs, which was Ubuntu 24.04 on
that date, and the pinned `ubuntu-24.04` (image `ubuntu24/20261018.339.1`,
Ubuntu 24.04.5 LTS) for the last one, which measured the tree this section
sits on. AMD EPYC 9V74 / 9V45, 4 vCPU (`nproc`=4, `/proc/stat` accounts for
4 CPUs, affinity `0-3`, no cgroup quota), .NET SDK 10.0.402. The harness is
pinned to `ubuntu-24.04` from here on, for the reason ci.yml's build job
gives: the label moves, and a CPU-utilisation number without the image it was
measured on is a number without a denominator. The restore is timed
separately and is in none of the numbers below.

**The measured compile is a COLD one.** Every run starts from a fresh
checkout with no `obj/`, so it measures a from-scratch Release compile of the
whole tree. The PR lane's build is incremental since #1927's build-output
cache landed (it restores the base branch's `obj/` and rebuilds what
changed), so the PR lane's wall time is NOT the number below.

**The meter is calibrated, not asserted.** A known 1-way load reads 1.11 busy
cores and a known 4-way load reads 4.00 (`calib-1-way` / `calib-4-way` in the
run artifacts). An earlier version of the harness divided by the machine's
total jiffies and so reported a FRACTION of the machine, where "99 % busy"
and "one core of four" are the same number; the divisor is now the number of
CPUs `/proc/stat` accounts for.

| arm | what changes | n | wall (s) | avg busy cores of 4 | at ≥ 3.5 cores | `Csc` tasks | `Csc` task time | gate (s) | wall ratio | ratio without the gate |
|---|---|---:|---|---|---|---:|---|---|---|---|
| A | the CI command as it is | 15 | 253.4..409.0 (median 348.7) | 3.27..3.61 | 56..75 % | 112 | 266..498 s | 79..154 | 1.00 | 1.00 |
| B | `-p:MaxCpuCount=1` | 15 | 298.8..399.8 (median 378.0) | 3.28..3.60 | 54..76 % | 112 | 313..487 s | 94..148 | 1.08 | 1.04 |
| C | `-maxcpucount:1` | 15 | 244.1..379.8 (median 354.6) | 2.90..3.23 | 30..53 % | 112 | 129..225 s | 70..135 | **1.02** | **0.95** |
| D | `-p:UseSharedCompilation=false` | 9 | 487.8..911.4 (median 858.9) | 3.09..3.60 | 53..78 % | 112 | 681..1673 s | 71..193 | 2.46 | 2.99 |

**The falsification ratio is 1.0, not 4.** Arm C is one MSBuild node, one
project at a time, and it is not slower: 1.02 of the baseline on the wall,
0.95 with the architecture gate taken out, and every one of arm C's fifteen
wall times (244.1..379.8 s) lies inside arm A's fifteen (253.4..409.0 s). The
runner's own speed moves a build by up to 2x between jobs, so every run's gate
duration is kept as the speed proxy: the architecture gate is the same
single-process workload in every arm, and dividing each run by its own gate
puts both arms at 1.0.

**Why the ratio is 1.0: one compilation already uses three of the four
cores.** The per-process accounting says it directly. `VBCSCompiler` (the
Roslyn compiler server) consumed 433..722 CPU-seconds in arm C, where
exactly ONE compilation was in flight, over a 169..244 s compile phase: that
is 2.6..3.0 cores per compilation. In arm A, with four nodes, the same
process consumed 511..866 CPU-seconds over a 174..280 s compile phase: 2.9..3.6
cores. The number is the same in both arms. The four vCPU are therefore
saturated from INSIDE a single compilation (Roslyn's parallel analyzer
driver), not by running several projects at once, so more MSBuild nodes cannot
buy more throughput: the same four cores are already busy. The `Csc` task
time tells the same story from the other side, 129..225 s in arm C against
266..498 s in arm A for the same 112 compilations: in arm A each `Csc` task
spends most of its time waiting for a CPU that is busy anyway.

**The graph is not the limiter either.** Measured, not modelled: at least four
projects were in flight for 60.7..99.2 % of the build (peak 75..83 in
flight, average 25..33). The graph agrees
(`tools/measure-project-graph.py`, on this branch's tree): 109 projects, 465
edges, depth 15, widest level 20 projects, and 11 of its 16 levels are wide
enough for four compilers.

**What the verdict is for #1929.** The issue's decision table cannot be used
as written: the ratio it asks for reads 1.0, which the table maps to "the
graph is the bottleneck, the model is wrong". Neither half is right. The
graph is not the bottleneck, and the node count is not the lever. What IS
true is that the compile phase is CPU-throughput-bound on the runner's four
cores.

**The prescribed property is inert.** `-p:MaxCpuCount=1` (arm B) cannot be
told from the baseline: 1.08 on the wall, 1.04 after the speed
normalisation, the same 112 `Csc` tasks, the same 3.3..3.6 busy cores. MSBuild
reads the `-maxcpucount` SWITCH; the `MaxCpuCount` property appears in no
MSBuild target and in no global-property lookup. The repository's own
`-p:MaxCpuCount=0` (`build/_build/Targets/CompileTarget.cs:22` and `:34`) is
a no-op for the same reason: `dotnet build` is parallel on its own, which is
why arm A, arm B and the CI-exact calibration all read 3.3..3.6 busy cores.

**Practical conclusion: two jobs are worth it, but not for the reason in the
issue, and not for the number in the issue.**

* The compile phase is the analyzers inside each compilation, and it is
  throughput-bound on four cores. More CORES buy more throughput; more
  MSBuild NODES do not. A second job on a second runner gives the analyzer a
  second set of four cores, so the compile phase (238 s of arm A's 349 s
  median wall) could roughly halve, to ~120 s.
* The split has to be two INDEPENDENT clusters of the graph. The layer split
  the issue proposes first ("src+apps | tests") buys nothing, because the
  test projects depend on the product projects, so the halves run one after
  the other.
* The architecture gate caps the win on the lanes where it still runs. It is
  a single process running the whole architecture suite: 79..154 s, 29..39 %
  of the build, and it can only run after both halves are compiled. On the
  PR lane the gate is off (dev switches it off there), so the ceiling on
  that lane is the halves' compile time alone; on the push and nightly
  lanes, where the gate still runs, the realistic ceiling is ~120 s of
  compile plus ~120 s of gate = ~240..260 s against 348 s now, about 1.4x,
  not the 209 s the issue quotes.
* `-p:UseSharedCompilation=false` (arm D) is not the answer: 2.5x slower on
  the wall (487.8..911.4 s), because every compilation then pays the Roslyn
  start-up cost and four of them oversubscribe the same four cores.
* The levers this measurement points at instead: the analyzers themselves
  (the compile phase is analyzer-bound, so `RunAnalyzers=false` in the build
  path with the analyzers moved to a gate job is the obvious next
  measurement, not a claim), the `obj/`+`bin/` cache (#1927), and the
  architecture gate out of the build step (#1920, worth the measured
  79..154 s).

**What this does not say.** It does not measure a two-job split, so the ~1.4x
above is a projection from the measured CPU work, not a measured split. It
does not attribute the compile phase between the analyzers and the actual
compilation. And it does not measure a runner with more than 4 vCPU.

**Where this sits against the nightly lane (#1923).** The nightly lane's
`measurement` job owns the PERIODIC measurement of the same Release compile
(the binary log, the analyzer profile, the CPU samples on a schedule and on
every merge into dev/master). This section is the ONE-OFF falsification for
issue #1929, and its harness stays on its branch; it is not a second lane.
Two corrections that lane's probes need, both measured here:

1. Its serial probe has to use the SWITCH. `-p:MaxCpuCount=1` is inert:
   MSBuild reads `-maxcpucount` as a switch, and the `MaxCpuCount` property
   appears in no MSBuild target and in no global-property lookup. A probe
   that names the property measures the all-cores build twice, and reads
   1.0 for the wrong reason.
2. The architecture gate has to be out of a compile-phase measurement, or
   subtracted from it. It is 29..39 % of the wall (79..154 s of a
   253..409 s build) and it is a single process, so it is serial in every
   arm. The nightly job switches it off (`HarborArchGate: 'false'`), which
   is the right shape; this harness keeps it on, because it measures the
   shape the NUKE `Compile` target runs, and subtracts it in the "ratio
   without the gate" column.

The three tools are the reusable instrument (`tools/measure-build-cpu.py`
runs a command and meters it, `tools/aggregate-build-vcpu.py` turns the runs
into the table, `tools/measure-project-graph.py` parses the graph), and the
nightly lane's analysis step can read the same measurements instead of
growing a second parser.

### 6.8 Coverage per project (#1753) ✅ measured in the nightly lane

Measured on the nightly run [39528039639](https://github.com/refusedguy/Harbor-Harness/actions/runs/39528039639), branch `ci/1753-per-file-coverage` at `c480a98f`, 2026-10-21. The lane is the `coverage` job of `.github/workflows/nightly.yml`; the reader is [`tools/coverage_report.py`](../tools/coverage_report.py), the registry is [`tools/coverage-registry.txt`](../tools/coverage-registry.txt), and the gate is [`tools/check-coverage-registry.py`](../tools/check-coverage-registry.py).

**WHAT THIS NUMBER IS NOT.** There is no global percentage here, on purpose. The number the lane used to publish was a sum over the per-suite columns, and those columns OVERLAP on the shared `src/` assemblies — the same line is counted once per suite that touched it — so the sum was never the coverage of anything, and the report said so in five words ("the TOTAL is indicative"). What is below is per project and per file, with the scope written down.

**THE SCOPE.** The twenty core+platform unit suites. The ui, e2e and load suites are not part of the lane: they are slow, they need a display, and their flakes (#423) would make the number unreadable. Two of the product areas therefore sit OUTSIDE the measurement, and the registry says so per file instead of the number averaging them away.

**THE FOUR STATES OF A FILE.** The engine is the oracle, not a guess: it instruments whole modules and reports every file of them that has something to instrument.

| state | what it means | files |
|---|---|---|
| covered | at least one line was hit | 497 |
| never ran | the engine saw the file and not one of its lines ran | 373 |
| not loaded | the file's project is not in this lane at all | 208 |
| no code to cover | the engine reported nothing for the file: an interface, an enum, a global-using file, a source-generated context | 172 |

**THE PRODUCT, PER PROJECT** (1250 files under `src/` and `apps/`)

| project | files | covered | never ran | not loaded | no code to cover |
|---|---|---|---|---|---|
| `src/Harbor.Tui.CellForge` | 113 | 30 | 82 | 0 | 1 |
| `src/Harbor.Tui.CellForge.Engine` | 91 | 4 | 75 | 0 | 12 |
| `src/Harbor.Ui.Framework.Rendering` | 50 | 7 | 32 | 0 | 11 |
| `src/Harbor.Ui.Framework.State` | 51 | 23 | 22 | 0 | 6 |
| `src/Harbor.Ui.Framework.Services` | 32 | 3 | 23 | 0 | 6 |
| `src/Harbor.Desktop.Abstractions` | 30 | 1 | 23 | 0 | 6 |
| `src/Harbor.Abstractions` | 61 | 34 | 6 | 0 | 21 |
| `src/Harbor.Application` | 29 | 22 | 1 | 0 | 6 |
| `src/Harbor.Abstractions.Contracts` | 41 | 32 | 5 | 0 | 4 |
| `src/Harbor.Tools.Builtin` | 69 | 59 | 1 | 0 | 9 |
| `src/Harbor.Hosting` | 35 | 32 | 2 | 0 | 1 |
| `src/Harbor.Terminal.Abstractions` | 27 | 5 | 16 | 0 | 6 |
| `src/Harbor.Ui.Framework.Projection` | 19 | 2 | 11 | 0 | 6 |
| `src/Harbor.Ui.Framework.Abstractions` | 18 | 0 | 10 | 0 | 8 |
| `src/Harbor.Ui.Framework.Sessions` | 15 | 2 | 8 | 0 | 5 |
| `src/Harbor.Ui.Framework.ViewModels` | 10 | 1 | 9 | 0 | 0 |
| `src/Harbor.Plugins.Abstractions` | 16 | 6 | 1 | 0 | 9 |
| `src/Harbor.Plugins.Hosting` | 9 | 7 | 1 | 0 | 1 |
| `src/Harbor.Plugins.Storage` | 8 | 6 | 1 | 0 | 1 |
| `src/Harbor.Plugins.Host` | 4 | 2 | 2 | 0 | 0 |
| `src/Harbor.DesignSystem` | 14 | 4 | 5 | 0 | 5 |
| `src/Harbor.Desktop.Shared` | 9 | 0 | 7 | 0 | 2 |
| `src/Harbor.Desktop.Animations` | 5 | 0 | 3 | 0 | 2 |
| `src/Harbor.Extensions` | 5 | 2 | 2 | 0 | 1 |
| `src/Harbor.Tui.AnsiPlain` | 9 | 3 | 2 | 0 | 4 |
| `src/Harbor.Tui.CellForge.Syntax` | 9 | 0 | 8 | 0 | 1 |
| `src/Harbor.Tui.CellForge.Prompts` | 3 | 1 | 1 | 0 | 1 |
| `src/Harbor.Tui.Notifications` | 1 | 0 | 1 | 0 | 0 |
| `src/Harbor.Terminal.Pty` | 4 | 0 | 4 | 0 | 0 |
| `src/Harbor.Logging` | 1 | 0 | 1 | 0 | 0 |
| `src/Harbor.Telemetry.Otlp` | 1 | 0 | 1 | 0 | 0 |
| `src/Harbor.Transport.Remote` | 5 | 3 | 1 | 0 | 1 |
| `src/Harbor.AgentRuntime` | 33 | 29 | 0 | 0 | 4 |
| `src/Harbor.Storage.Jsonl` | 21 | 20 | 0 | 0 | 1 |
| `src/Harbor.Ipc.Abstractions` | 20 | 15 | 0 | 0 | 5 |
| `src/Harbor.SessionContext` | 18 | 16 | 0 | 0 | 2 |
| `src/Harbor.Ipc.Server` | 14 | 13 | 0 | 0 | 1 |
| `src/Harbor.PolicyConfig` | 13 | 9 | 0 | 0 | 4 |
| `src/Harbor.ChangeTracking` | 12 | 11 | 0 | 0 | 1 |
| `src/Harbor.Ipc.Client` | 12 | 10 | 0 | 0 | 2 |
| `src/Harbor.Registries` | 12 | 10 | 0 | 0 | 2 |
| `src/Harbor.Telemetry.Core` | 10 | 10 | 0 | 0 | 0 |
| `src/Harbor.Permissions` | 8 | 6 | 0 | 0 | 2 |
| `src/Harbor.Storage.Sqlite` | 8 | 7 | 0 | 0 | 1 |
| `src/Harbor.Plugins.Registration` | 7 | 6 | 0 | 0 | 1 |
| `src/Harbor.Hooks` | 6 | 3 | 0 | 0 | 3 |
| `src/Harbor.Plugins.Compilation` | 6 | 5 | 0 | 0 | 1 |
| `src/Harbor.CodeGen` | 6 | 0 | 6 | 0 | 0 |
| `src/Harbor.Providers.OpenAiCompatible` | 5 | 3 | 0 | 0 | 2 |
| `src/Harbor.Providers.Shared` | 5 | 5 | 0 | 0 | 0 |
| `src/Harbor.Lsp` | 4 | 4 | 0 | 0 | 0 |
| `src/Harbor.Onboarding` | 4 | 3 | 0 | 0 | 1 |
| `src/Harbor.Storage.Shared` | 4 | 4 | 0 | 0 | 0 |
| `src/Harbor.Diagnostics.Abstractions` | 3 | 2 | 0 | 0 | 1 |
| `src/Harbor.Plugins.Instantiation` | 3 | 2 | 0 | 0 | 1 |
| `src/Harbor.Plugins.Runtime` | 3 | 2 | 0 | 0 | 1 |
| `src/Harbor.Providers.Anthropic` | 3 | 3 | 0 | 0 | 0 |
| `src/Harbor.Providers.OpenAI` | 3 | 3 | 0 | 0 | 0 |
| `src/Harbor.Ipc.InProcess` | 2 | 2 | 0 | 0 | 0 |
| `src/Harbor.Storage.Memory` | 2 | 2 | 0 | 0 | 0 |
| `src/Harbor.Providers.Ollama` | 1 | 1 | 0 | 0 | 0 |
| `apps/Harbor.App.Cli` | 115 | 0 | 0 | 115 | 0 |
| `apps/Harbor.App.Avalonia` | 93 | 0 | 0 | 93 | 0 |
| **total** | **1250** | **497** | **373** | **208** | **172** |

**THE LINE COUNT, for the files the engine instrumented.** 61 539 product lines, 27 228 of them hit (44.2 %). The 23 316 lines of the 373 "never ran" files are the other half of that gap, and they are the deletion candidates: a file nobody ever ran is the strongest form of the "an uncovered line is often dead code" rule (#772, #755).

**THE SHARE OF CONSCIOUSLY UNCOVERED CODE.** 581 of the 1250 product files (46.5 %) are holes of this measurement, and every one of them is in the registry with a reason: 373 file rows for the files the measurement saw and never ran, and 2 project rows (`apps/Harbor.App.Cli/`, `apps/Harbor.App.Avalonia/`) for the two projects the lane does not load at all. The registry is a RATCHET in both directions: a file that stops being covered has to have a row added, and a row whose file got covered has to have it deleted — the gate fails on either.

**THE MEASUREMENT WAS EMPTY BEFORE THIS.** Run 39508517419 uploaded twenty cobertura files and every one of them was `<coverage line-rate="1"><packages /></coverage>`; every suite printed "Total - Line: N/A" and the summary printed "No cobertura files produced". The cause: Release sets `DebugType=none` (`Directory.Build.props`), so the build carries no PDB, and the coverage engine on Linux instruments the assemblies STATICALLY — the instrumentation maps IL back to source lines through the portable PDB next to each DLL. With no PDB there is nothing to instrument, and an empty report reads as "100 % covered" to anything that parses `line-rate`. The lane now builds with `-p:DebugType=portable -p:DebugSymbols=true`.

## 7. Comparison with previous (inflated) numbers

> "Reality (this doc)" points at the section each figure lives in, so every row inherits that
> section's provenance — §3/§4 (§1 environment, 2026-07-18) for binary size, RSS and cold start;
> §6.1 (CI run 39434075758, dev, 2026-10-20) for the test rows.

| Metric | Old claim | Reality (this doc) | Why differed |
|---|---:|---:|---|
| Binary size | 5 MB | 109 MB (publish folder) | Old was Debug DLL alone, not publish folder |
| RSS idle | 28 MB | ~80-120 MB (estimated) | Old was Debug `--version` exit, not real session |
| Cold start | 38 ms | 966 ms | Old was Debug incremental, not Release from cold cache |
| Test count | 242 | **8 342 discovered** (§6.1, run 39434075758) | Old was outdated; suite grew with new features |
| Test duration | 12 s | **268.9 s** of test time, `core` shard 140 s (§6.1) | Old was after warm build, 12 projects, Debug, no-build; the suite is now 37 projects and runs in four Release shards in parallel |
| Architecture suite | 45 tests / 2.19 s (local Debug, 2026-07-18) | **1 032 discovered / 78.75 s** (CI, Release, §6.1) | Same: the old row measured a tree 23x smaller, in Debug, without the CI gate |

## 8. Per-app assessment

### 8.1 CLI (`apps/Harbor.App.Cli`)

| Aspect | Status | Notes |
|---|---|---|
| Build | ✅ green | 0 warnings, 0 errors |
| Tests | ✅ 8 306 passed of 8 342 discovered | CI, Release, four shards (§6.1); 35 skipped, 1 failed (an E2E Avalonia test, §6.1) |
| Binary | 109 MB publish | Roslyn + Spectre dominate |
| AOT | ✅ publishes (`fulltree`) | 29 MB self-contained binary, §4.4 |
| Cold start | 18 ms AOT / 138 ms JIT (`--version`) | Measured §4.4 |

### 8.2 Avalonia (`apps/Harbor.App.Avalonia`)

| Aspect | Status | Notes |
|---|---|---|
| Build | ✅ green | 0 warnings, 0 errors |
| Run | ⚠️ untested in sandbox | No display server; needs real desktop |
| Binary | 207 KB DLL | + ~140 MB publish folder (estimated) |
| Features | code editor, sessions, command palette, diff, charts, toasts, themes | Production-ready |

### 8.3 WPF (`contrib/apps/Harbor.App.Wpf`)

| Aspect | Status | Notes |
|---|---|---|
| Build | ✅ green (Linux sandbox) | `EnableWindowsTargeting=true` |
| Run | ❌ Windows only | Cannot run on Linux |
| Binary | 125 KB DLL | + ~120 MB publish (estimated) |

### 8.4 MAUI (`contrib/apps/Harbor.App.Maui`)

| Aspect | Status | Notes |
|---|---|---|
| Build | ⚠️ needs MAUI workload | `dotnet workload install maui-windows` |
| Run | ❌ untested | Skeleton only — no real UI |
| Status | Draft | Needs CollectionView + Entry + chat page |

### 8.5 Blazor (`contrib/apps/Harbor.App.Blazor`)

| Aspect | Status | Notes |
|---|---|---|
| Build | ✅ green | Kestrel on http://localhost:5000 |
| Run | ⚠️ untested | Launches browser, needs API key |
| Binary | 132 KB DLL | + ~115 MB publish (estimated) |

## 9. Honest assessment

### What's actually fast

- **Microbenchmarks**: hot paths (`ProviderRegistry.GetClient`, `ToolRegistry.ResolveTools`, `PermissionRuleset.Evaluate`, `UiStore.Dispatch`) are sub-microsecond with zero allocations — the `ArrayPool`/`StringBuilderPool`/`FrozenDictionary`/`StringPool` strategy works.
- **Test suite**: 8 342 discovered tests, 268.9 s of test time, `core` shard 140 s (§6.1). TUnit source-gen is fast, and the four shards run in parallel.
- **Lock-free `UiStore.Dispatch`**: 0.15 µs via CAS loop, no contention.

### What's actually slow / bloated

- **Cold start 966 ms** (old `dotnet`-host number): measured 18 ms AOT / 138 ms JIT on `--version` (§4.4) — the <100 ms target is met as a measurement.
- **Publish folder 109 MB**: Roslyn (30+ MB for plugin compilation) is the elephant. Moving plugin host out-of-process would cut ~30 MB.
- **`JsonlSessionStore.GetMessages`**: 850 µs for 100 messages, 28 KB allocated — measured before the `Utf8JsonReader` span rewrite (`JsonlLineParser`); current behavior locked by #186 tripwires, re-measure before quoting.
- **AOT on `fulltree`: done** — publishes since #1055s3, 18 ms cold start measured (§4.4). Remaining AOT work is the `minimal` profile, not the main binary.

### What was misleading in old benchmarks

- "5 MB binary" was the Debug DLL, not the publish folder.
- "28 MB RSS idle" was a Debug `--version` run that exits immediately.
- "38 ms cold start" was Debug incremental ( assemblies already loaded in dotnet cache).
- These numbers were aspirational, not measured.

### Roadmap to actually hit "fast" targets

1. **Move Roslyn plugin host out-of-process** — DONE via #1055 (default out-of-proc route; the AOT publish completes, §4.4).
2. **Replace Spectre.Console with Spectre.TUI source-gen** — removes reflection, enables AOT.
3. **`Utf8JsonReader` rewrite for `JsonlSessionStore`** — 5x faster, 80% less allocation.
4. **NativeAOT publish for CLI** — DONE on `fulltree`: 29 MB binary, 18 ms cold start, ~29 MB RSS idle (§4.4). Open: the `minimal` profile.
5. **Self-contained + `PublishSingleFile` + trimmed** for desktop apps — Avalonia target: ~50 MB single .exe.

## 10. How to reproduce

All numbers in this doc are reproducible from the repo:

```bash
# Install .NET 10
wget https://dot.net/v1/dotnet-install.sh && ./dotnet-install.sh --channel 10.0

# Clone & restore
cd /path/to/harbor
export PATH="$HOME/.dotnet:$PATH"
dotnet restore

# Build
dotnet build -c Release

# Binary sizes
ls -lh apps/Harbor.App.Cli/bin/Release/net10.0/Harbor.App.Cli.dll
du -sh apps/Harbor.App.Cli/bin/Release/net10.0/

# Cold start (median of 3 runs)
for i in 1 2 3; do
  start=$(date +%s%N)
  dotnet run --no-build -c Release --project apps/Harbor.App.Cli -- --version >/dev/null
  end=$(date +%s%N)
  echo "Run $i: $(( (end - start) / 1000000 )) ms"
done

# Microbenchmarks
dotnet run -c Release --project tests/Harbor.Benchmarks -- --filter '*'

# Tests (per project as plain executables — never `dotnet test`, see §6.2)
dotnet build -c Release
dotnet run --project tests/Harbor.Core.Tests -c Release --no-build -- --minimum-expected-tests 1
```

## 11. See also

- [Architecture layers](./ARCHITECTURE_LAYERS.md) — why the project is split this way
- [Code principles audit](./CODE_PRINCIPLES_AUDIT.md) — performance findings and fixes
- [Plugin system](./PLUGIN_SYSTEM.md) — why Roslyn is in-process today (and the plan to move it out)
- [Desktop app plan](./DESKTOP_APP_PLAN.md) — desktop app size + perf targets
