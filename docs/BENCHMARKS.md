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
  the compiled `check-abstractions-contract` gate guards, and a `Microsoft.Extensions.ObjectPool`
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

#### The yardstick has a price, and the gate now pays it (run 39633684054)

The time branch is in **probe units**, so the runner's speed is supposed to
cancel: the numerator and the denominator are both measured on the same runner
in the same run. It cancels only while the yardstick IS the yardstick. The probe
is a fixed workload, so its cost is a property of the machine — and the machine
has a price: **0.000343-0.000353 ms/unit** in the nineteen measured
`test (ui)` runs of the fleet that read it (the oldest, run 39372950798, read
0.000380).

Three runs read **0.000041 ms/unit** instead — 8.5x below — while the workload
the probe was supposed to normalize was UNCHANGED:

| Run | probe ms/unit | frame ms/unit | frame probe units | verdict |
|---|---|---|---|---|
| 39633684054 | 0.000041 | 0.0815 | **1992.3** (budget 700) | red |
| 39609285153 | 0.000041 | 0.0816 | **1990.3** (budget 700) | red |
| 39619582982 | 0.000041 | — | — | red (patch split only) |
| 39639468264 (healthy) | 0.000350 | 0.0827 | 236.5 | green |
| 39654104203 (healthy, this branch) | 0.000348 | 0.0814 | 234.1 | green |

The numerator's spread was 1.01x in the red run — "reproducible" — so the
helper trusted the measurement and the ratio it asserted was 8.5x the cost the
runner actually produced. **A gate that fails on a number its runner never
produced is a red light, not a measurement.**

So the gate now declares the band the yardstick has to stay in
(`0.000150-0.000600 ms/unit` — 2.3x below and 1.6x above what the fleet
produces, so a slower or a faster machine still measures inside it). Outside
the band the time branch **reports and stands down** and the byte branch — the
machine-independent one, the branch that can be a hard gate — still asserts. The
budgets are untouched: the byte budgets (8 B / 4 KiB / 128 KiB) are the real
limits, and the time budgets stay in the currency the fleet is calibrated in.

The band also catches the other direction: run 39599923626 read 0.00072 ms/unit
in one of its six gates — 2x the fleet's price — and that gate stood down (its
own spread was 6.25x). A yardstick that reads 2x slow deflates every ratio
built on it, and a gate that passes because its yardstick was slow measures
nothing.

The warm-up of the patch split grew from 3 to 32 iterations at the same time:
the call-counting threshold is 30 calls, the measured window drives 8 splits
per round, and the short warm-up left the first measured rounds running tier-0
code. The measurement of run 39654104203 therefore reads **397.1** probe units
per split (0.1379 ms/unit against a probe of 0.000347 ms/unit) where the old
warm-up read 505.2 — the same code, measured honestly.

> The `[perf-gate] tool-start-event-cost: measured …` report lines print into
> the `test (ui)` job log on every run, and the compiled `test-measurements` gate lifts
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
| `SessionFileReaderScalingTests.ReadPath_CostPerRead_StaysWithinBudget` | allocation per full session-file read, over a FIXED 8 reads of a 4 000-message file | ratio ≤ 3.0 / 3.15 | **≤ 2 100 000 B per read** (HARD) | **1 693 591 B** per read (run 39550470314) — the append-only log (#1752) makes the reader materialise the log's RECORDS as well as the messages, so the history can be read back |
| `SessionFileReaderScalingTests.ReadPath_CostPerRead_StaysWithinBudget` | time per full session-file read, in probe units | ratio, above | **≤ 300 000 probe units per read** (SOFT) | **~11 600 probe units** per read — 4.03 ms/read (run 39550470314) |
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
> every run, and the compiled `test-measurements` gate lifts them into the job summary
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
the compiled `test-measurements` gate).

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
| **Report** | The report line is printed on every run, PASS OR FAIL, and says `measured` explicitly. The compiled `test-measurements` gate lifts it into the job summary (the #618 mechanism), so a gate that measures and reports without failing is still evidence |
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

**Source: CI run [39648943782](https://github.com/refusedguy/Harbor-Harness/actions/runs/39648943782)**
(pull request #2143, sha `64f9825b`, 2026-10-21 18:01 UTC), the five `test` shards of
`.github/workflows/ci.yml` on `ubuntu-24.04-arm`, .NET SDK 10.0.402, Release
binaries built by the same run's `build` job.

This is the first run of the `ci` lane since #2115 broke the workflow FILE
itself: the arm64 reporting steps had lost their list marker, so `ci.yml` was
not a workflow and every run of it created no jobs at all, on `dev` and on
every branch alike (#2146 fixed it). The run before it on the same tree
(39643565186) failed in the `core` shard on
`ParentCancel_StopsBackgroundChildren`; the re-run of that job passed, and
this run is green end to end.

Every number below is **MEASURED**, and none of it is a projection:

- **Discovered** is the runner's own `total:` from its "Test run summary" line,
  which counts the tests the runner DISCOVERED. It is not the passed count: the
  `Passed` column is separate, and the gap between the two is the skipped
  tests, not a missing measurement.
- **Duration** is the runner's per-assembly duration.
- The shard wall is the job step's own wall (`Run shard <shard>`).

| Test project | shard | Discovered | Passed | Failed | Skipped | Duration |
|---|---|---:|---:|---:|---:|---:|
| `Harbor.Application.Tests` | core | 671 | 671 | 0 | 0 | 7.36 s |
| `Harbor.Core.Tests` | core | 285 | 285 | 0 | 0 | 9.66 s |
| `Harbor.Abstractions.Tests` | core | 280 | 280 | 0 | 0 | 2.59 s |
| `Harbor.ChangeTracking.Tests` | core | 126 | 126 | 0 | 0 | 5.19 s |
| `Harbor.Registries.Tests` | core | 124 | 124 | 0 | 0 | 1.90 s |
| `Harbor.Config.Tests` | core | 122 | 122 | 0 | 0 | 1.99 s |
| `Harbor.TestKit.Tests` | core | 121 | 121 | 0 | 0 | 0.79 s |
| `Harbor.Hosting.Tests` | core | 118 | 118 | 0 | 0 | 1.40 s |
| `Harbor.Domain.Tests` | core | 58 | 58 | 0 | 0 | 2.26 s |
| `Harbor.Lsp.Tests` | core | 35 | 35 | 0 | 0 | 1.29 s |
| `Harbor.Telemetry.Tests` | core | 25 | 25 | 0 | 0 | 0.76 s |
| `Harbor.Terminal.Pty.Tests` | core | 14 | 14 | 0 | 0 | 0.60 s |
| `Harbor.CodeGen.Tests` | core | 9 | 9 | 0 | 0 | 1.56 s |
| `Harbor.TestKit.Generators.Tests` | core | 4 | 4 | 0 | 0 | 1.80 s |
| `Harbor.Tools.Builtin.Tests` | platform | 533 | 531 | 0 | 2 | 4.86 s |
| `Harbor.Storage.Jsonl.Tests` | platform | 211 | 211 | 0 | 0 | 4.91 s |
| `Harbor.Plugins.Runtime.Tests` | platform | 196 | 196 | 0 | 0 | 5.00 s |
| `Harbor.Storage.Tests` | platform | 157 | 157 | 0 | 0 | 3.71 s |
| `Harbor.Providers.Tests` | platform | 154 | 154 | 0 | 0 | 2.45 s |
| `Harbor.Ipc.Tests` | platform | 133 | 129 | 0 | 4 | 4.89 s |
| `Harbor.Plugins.Abstractions.Tests` | platform | 33 | 33 | 0 | 0 | 0.54 s |
| `Harbor.Plugins.Host.Tests` | platform | 19 | 19 | 0 | 0 | 0.57 s |
| `Harbor.Transport.Remote.Tests` | platform | 17 | 17 | 0 | 0 | 0.80 s |
| `Harbor.Tui.CellForge.Tests` | ui | 2 480 | 2 480 | 0 | 0 | 6.75 s |
| `Harbor.Ui.Framework.Tests` | ui | 637 | 637 | 0 | 0 | 5.38 s |
| `Harbor.App.Avalonia.Tests` | ui | 377 | 370 | 0 | 7 | 2.05 s |
| `Harbor.Tui.Tests` | ui | 248 | 248 | 0 | 0 | 0.64 s |
| `Harbor.Terminal.Abstractions.Tests` | ui | 76 | 76 | 0 | 0 | 0.80 s |
| `Harbor.Tui.CellForge.PtyTests` | ui | 64 | 64 | 0 | 0 | 34.30 s |
| `Harbor.DesignSystem.Tests` | ui | 53 | 53 | 0 | 0 | 0.78 s |
| `Harbor.Tui.RendererTests` | ui | 37 | 37 | 0 | 0 | 0.53 s |
| `Harbor.App.Cli.Tests` | e2e | 370 | 370 | 0 | 0 | 1.48 s |
| `Harbor.Tui.E2E.Tests` | e2e | 30 | 29 | 0 | 1 | 0.55 s |
| `Harbor.E2E.Framework.Tests` | e2e | 30 | 30 | 0 | 0 | 0.51 s |
| `Harbor.LoadTests` | e2e | 16 | 16 | 0 | 0 | 1.41 s |
| `Harbor.E2E.Cli` | e2e | 11 | 11 | 0 | 0 | 3.78 s |
| `Harbor.E2E.App.Avalonia` | e2e | 107 | 100 | 0 | 7 | 15.30 s † |
| `Harbor.Architecture.Tests` | arch | 1 118 | 1 118 | 0 | 0 | 97.46 s |
| **Total (38 projects, 5 shards)** | | **9 099** | **9 078** | **0** | **21** | **238.6 s** |

† `Harbor.E2E.App.Avalonia` runs as three processes
(`tests/Harbor.E2E.App.Avalonia/e2e-shards.json`), so its row is the CRITICAL
PATH of those processes (15.3 s of 12.4 / 15.3 / 4.9 s), not a sum.
**Shard walls, same run** (`Run shard <shard>` step): `arch` **99 s**, `core`
**48 s**, `ui` **56 s**, `platform` **33 s**, `e2e` **27 s**. The PR wall is the
maximum shard, not the sum, so `arch` is the binding shard on this run — it
has been since the architecture suite got a shard of its own (#1781).

**The `e2e` shard costs 27 s**, and the E2E framework's own project is 0.51 s
of it (`Harbor.E2E.Framework.Tests`, 30 tests): the framework is not the
shard's cost, the Avalonia app is (15.3 s of the 27 s, as three processes).

**How to reproduce the table.** The counts and the per-assembly durations are in
the run's job log (the "Test run summary" block per project). They are also in
the TRX files, and the compiled `test-census` gate turns those into exactly this table,
per project and per test:

```bash
gh run download 39648943782 --name test-results-core --dir TestResults/core
tools/bin/harbor-gates-linux-x64 test-census TestResults/core          # the table above
tools/bin/harbor-gates-linux-x64 test-census TestResults/core --format csv > census.csv
```

The census reads every result the runner produced, including the ~99% of tests
that printed nothing, so its `discovered` column is the number of tests the
suite has, and its `wall` column is the runner's per-assembly duration read
from the TRX `<Times>` window. On run 39648943782 the census and the job log
agree to the test: 9 099 discovered, 9 078 passed, 21 skipped, 0 failed, in the
same 38 projects.
the compiled `test-measurements` gate cannot do this: it renders only the tests that
printed a measurement, which on a full run is 88 rows out of 9 099.

> **The TRX artifacts came back in this PR.** They were gone from every run
> between 2026-10-20 11:17 and this one. The TUnit 1.75 upgrade (`b6fcc333`)
> changed how the runner resolves a relative `--results-directory`: the TRX
> files landed under the project's own output directory
> (`tests/<project>/bin/Release/net10.0/TestResults/<shard>/`) instead of the
> workspace root, so neither the artifact upload (`TestResults/**`, relative
> to the workspace) nor the compiled `test-measurements` gate could see them, and every
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
> The suite grew from ~493 to 9 099 discovered tests (18x) and the architecture
> suite from 45 to 1 118 (25x) between that measurement and this one. A number
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
| [39434075758](https://github.com/refusedguy/Harbor-Harness/actions/runs/39434075758) (dev, `07769e7e`, 13:51) | the §6.1 baseline run before this one | **1 032** | **1 032** | **78.75 s** | 1 053 |
| [39450959681](https://github.com/refusedguy/Harbor-Harness/actions/runs/39450959681) (PR branch, `f2491032`, 15:5x) | after #1791's shared tree snapshot | 1 068 | 1 068 | 89.43 s | 1 089 |
| [39648943782](https://github.com/refusedguy/Harbor-Harness/actions/runs/39648943782) (PR #2143, `64f9825b`, 18:01) | the §6.1 baseline run now | **1 118** | **1 118** | **97.46 s** | 1 143 |

The last column is the source-level `[Test]` count in
`tests/Harbor.Architecture.Tests/`, which runs 15-25 above the discovered
count on every one of those trees (the difference is `[Test]` occurrences that
the runner does not discover). It is a check on the measurement, not a
substitute for it.

**Read the count, not the wall.** The runs above discovered 966, 977, 998,
1 002, 1 032, 1 068 and 1 118 tests, and the wall ranged from 65.88 s to
122.10 s for
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
the compiled `test-census` gate): 1 032 tests, a sum of per-test durations of **1 888.72
s** and a wall of **132.60 s**, a parallelism factor of **14.24**. So the suite
is not one serial chain: it runs about fourteen lanes at once. The wall is set
by the slowest single test, not by the total: the four
`PanelKeyRouteReachabilityRule` tests take 112 s each, the pilot Roslyn analyzer
test 108.0 s, and the `ProcessEnvIsolationRule` family 96-99 s each. The same
numbers on the older tree of run 39399653820 (977 tests): 1 108.01 s of test
time in a 95.39 s window, a parallelism factor of 11.62, and a floor set by
79 s tests. The shape is the same on both; the tree grew by 5.5 %.

**The closest thing to the spike's own question, already measured.**
`ArchAnalyzerPromotionTests` runs a Roslyn analyzer over the live tree, which is
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

**What the gate is, and why it is not a timing gate.** The shard-balance gate
(`tools/gates/check-shard-balance`, the compiled port of the python original)
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
> upload nor the compiled `test-census` gate finds the file. That is a `dev` regression and
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
| `ArchAnalyzerPromotionTests` | 66.2 s | 169.6 s | **52 Roslyn COMPILATIONS** (1 236 files parsed) — not a text scan |
| `TokenCellSingleHomeRules` | 28.8 s | 28.9 s | a text scan of the product trees against three regexes — one test, so the class's parallelism is 1.00 by construction |
| `ApprovalGateWaiterRaceRules` | 19.1 s | 38.5 s | a text scan of the whole test tree, twice |

**After** — two runs of the same tree plus the change, 2026-10-20: 39418488276
and 39421786081.

| class | span (before) | span (after) | durations (before) | durations (after) |
|---|---:|---:|---:|---:|
| `PanelKeyRouteReachabilityRule` | 72.9 s | 58.6 / 58.9 s | 502.8 s | 235.2 / 237.4 s |
| `ProcessEnvIsolationRule` | 67.9 s | **1.9 / 1.4 s** | 632.2 s | **11.4 / 7.7 s** |
| `ArchAnalyzerPromotionTests` | 66.2 s | 51.4 / 55.4 s | 169.6 s | 94.2 / 110.4 s |
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

### 6.5.2 The architecture suite's parser and its Roslyn sweep (#2110) — measured

The `arch` shard's wall was set by the layer UNDER the rules: the text
parser every rule reads the tree through, and the Roslyn analyzer driver,
which used to walk the same 64 product compilations FOUR times.

**The wall**, `ubuntu-24.04-arm`, the numbers from the `arch` shard's TRX.
The first two columns are on the SAME base commit (`a019e928`), so the
difference between them is the branch's work and nothing else:

| | dev @ `a019e928` (run 39717885532) | the branch @ `a019e928` (run 39718941803) | the branch's later runs (39721154583, 39722755948, 39723469087) | |
|---|---:|---:|---:|---|
| tests discovered / passed | 1129 / 1129 | 1136 / 1136 | 1136 / 1136 | |
| **wall (the TRX `<Times>` window)** | **106.63 s** | **81.91 s** | 85.00 / 80.00 / 83.00 s | −22.1 … −25.0 % |
| sum of the per-test durations | 1325.38 s | 1010.60 s | 1037.64 / 924.00 / 1026.15 s | −22.6 … −30.3 % |
| parallelism factor | 12.43× | 12.34× | 12.21× / 11.55× / 12.37× | |
| the `test (arch)` job | 127 s | 98 s | 99 / 102 / 96 s | −22 … −25 % |

Four green runs of the branch read 80.00 / 81.91 / 83.00 / 85.00 s against
the baseline's 106.63 s: a spread of 6.2 % of the wall, which is the
runner's own noise, and a median of 82.5 s — **−22.6 %**. The later runs
sit on a base two commits ahead of `a019e928` (the real MSBuild switches of
issue 2111 and the Go-source gate of issue 2078), and neither touches the
architecture suite.

**What moved**, the class spans (last end − first start) and the single
tests, from the same TRX sets:

| class | dev | the branch | |
|---|---:|---:|---|
| `ArchAnalyzerPromotionTests` | 92.0 s | **27.0–31.0 s** | the sweep runs once, concurrently |
| `PanelKeyRouteReachabilityRule` | 84.0 s | 47.0–49.0 s | |
| `HttpClientCreationAnalyzerTests` | 60.0 s | 39.0–45.0 s | |
| `HttpClientReuseRules` | 60.0 s | 39.0–45.0 s | |
| `TokenCellSingleHomeRules` | 37.0 s | 23.0–25.0 s | |
| `UnguardedResultReadAnalyzerTests` | 27.0 s | 0.5 s | |

| test | dev | the branch | |
|---|---:|---:|---|
| `ArchAnalyzerPromotionTests.The_Namespace_Rule_Is_Clean_On_The_Live_Tree_And_The_Analyzer_Is_Cheap` | 90.80 s | 31.31 s | |
| `ArchAnalyzerPromotionTests.The_Analyzer_Sees_Every_File_The_Text_Rule_Sees` | 64.02 s | 31.34 s | |
| `UnguardedResultReadAnalyzerTests.The_Analyzer_Sees_Every_Site_The_Text_Rule_Sees` | 27.14 s | 0.52 s | |
| `DeadPublicMemberRule.EveryScopedPublicMember_IsReferencedOrTolerated` (the rule's own scan) | 2.44 s | 1.69 s | |

**The Roslyn sweep, measured inside the run.** The line the sweep prints
itself:

| | dev | the branch | |
|---|---:|---:|---|
| one driver pass over 64 product compilations | 63 372 ms | **31 301 ms** | −51 % |
| passes over the same compilations | 4 | **1** | |

**The parser, measured on its own.** The corpus is every `.cs` file of
`src/`, `apps/` and `tests/` — 2 676 files, 25 115 393 characters. The
sample is the 300 LARGEST of them (9 669 584 B), because a per-byte cost
measured on the biggest files is the worst case. The allocations are the
thread's own, read with `GC.GetAllocatedBytesForCurrentThread()`.

| | legacy | the substrate | |
|---|---:|---:|---|
| the identifier counter, allocations | 334 742 320 B | **27 149 664 B** | −91.9 % |
| the identifier counter, time | 257 ms | 188 ms | |
| the literal blanker, allocations | 38 708 448 B | **19 740 048 B** | −49.0 % |
| the literal blanker, time | 30 ms | 23 ms | |

**Roslyn is the modern API, and it does not pay.** On the same sample, the
text scan costs 24 ms and a Roslyn parse of the same bytes costs 668 ms —
**27.8× more**, and 141 761 584 B of garbage against 31 176 944 B. The
projection for the whole tree is 1 735 ms for the parse against a text pass
over the same tree. It also changes the VERDICTS: on all 40 of the largest
sampled files the text stripper and the Roslyn one disagree, because the
text stripper cannot tell a comment from a `//` inside a string literal and
eats the rest of such a line. The rules are written against that behaviour,
so a real parse is not an option here.

**The equivalence, and where it is proved.** The parser's own bytes are
proved identical to the previous implementation's over the whole corpus
(2 676 files, `blank-diffs=0`, `count-diffs=0`), and the rule whose inputs
the rewrite changed proves the same VERDICTS over the live tree against the
scan from before the rewrite. Neither proof is "the tests are still green":
a rule that finds nothing reports nothing.

**And the proofs can fail.** Run 39721911469 carried a PLANTED defect: the
one-pass member cursor was made to skip the first member of every type,
which is the exact defect a forward-only walk can have. Five tests went
red, and the five are the ones that have to:

| test | what it caught |
|---|---|
| `DeadPublicMemberEquivalenceTests.The_Member_Walk_Finds_The_Same_Members_As_The_Walk_Before_The_Rewrite` | the live-tree comparison: the two walks disagree about the graded surface |
| `DeadPublicMemberEquivalenceTests.A_File_With_Several_Types_Loses_No_Member` | the synthetic two-types control: the first type's members are missing |
| `DeadPublicMemberEquivalenceTests.The_Gate_Skips_The_Type_Parse_And_The_Verdict_Does_Not_Move` | the gate's own comparison |
| `DeadPublicMemberRule.NonVacuity_ASyntheticMemberWithNoReference_IsReported` | the rule stopped watching a member — its own load-bearing control |
| `DeadPublicMemberRule.ToleratedSurface_RowsAreStillDeadMembers` | a tolerance row's member stopped being graded |

So the rewrite does not weaken the rule: the rule still catches the rule
losing a member, and the new proofs catch the rewrite losing one.

**What holds the wall now.** Not the parsing. The class spans that remain:
`PanelKeyRouteReachabilityRule` 47.0–49.0 s (the backtracking
`MemberDeclaration` pattern over the product trees), `HttpClientReuseRules`
and `HttpClientCreationAnalyzerTests` 39.0–45.0 s each (the HTTP-client rule
of issue 1724), and the Roslyn sweep 27.0–31.0 s. The stripper is no longer
a term: the comment stripper's own cost is measured in
`ParseCostMeasurement`, and the scanner allocates 78.2 MB over the whole
tree against the legacy form's 12 587 MB.

> The `arch` shard's runner is noisy: a run whose `/run/user/<uid>/.dotnet`
> state is wrong fails the NuGet first-use migration of the probe that
> `CfeControlPositiveControlTests` spawns, and the NuGet cache that
> migration populates is what `BannedApiProbe` reads its analyzer from — so
> four unrelated tests go red together. Run 39718941803 failed that way on
> its first attempt and passed on the re-run of the same job with the same
> artifacts; the numbers above are from the green runs.

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

#### 6.4.1 The build job, step by step (#1788) ✅ measured in CI

Issue [#1788](https://github.com/refusedguy/Harbor-Harness/issues/1788) asks
what the `build` job is made of now, and what is left on the critical path.
This is the answer, step by step, from the job API (`started_at` /
`completed_at` per step), `ubuntu-24.04`, SDK 10.0.402, 2026-10-21.

**The lane as it was on `dev` that day**, three pull-request runs, the same
day, three different branches. The `build` job wall is the first job's start
to its end; the compile is the NUKE `Compile` step.

| step | [39533897704](https://github.com/refusedguy/Harbor-Harness/actions/runs/39533897704) | [39537006278](https://github.com/refusedguy/Harbor-Harness/actions/runs/39537006278) | [39536616567](https://github.com/refusedguy/Harbor-Harness/actions/runs/39536616567) |
|---|---:|---:|---:|
| checkout | 3 s | 3 s | 3 s |
| setup .NET 10 (+ the NuGet cache) | 23 s | 25 s | 23 s |
| compute the cache key | 0 s | 0 s | 0 s |
| **restore obj/ + bin/** | **1 s (nothing found)** | **0 s (nothing found)** | **0 s (nothing found)** |
| **normalise the source mtimes** | **skipped** | **skipped** | **skipped** |
| **NUKE compile (Release)** | **261 s** | **213 s** | **267 s** |
| banned-assembly gate | 0 s | 1 s | 0 s |
| CLI smoke | 0 s | 0 s | 1 s |
| installer scripts + build catalog | 2 s | 1 s | 2 s |
| pack NuGet libraries | 13 s | 9 s | 15 s |
| validate the packages | 0 s | 0 s | 1 s |
| determinism (two packs) | 11 s | 9 s | 12 s |
| clean-room smoke-install | 5 s | 5 s | 8 s |
| upload the nupkg artifact | 3 s | 3 s | 3 s |
| advisory report (only when the deps moved) | 34 s | skipped | skipped |
| upload the arch-gate log | 0 s | 0 s | 0 s |
| **`build` job wall** | **360 s** | **274 s** | **341 s** |

**The compile is 60-80 % of the job, and it has always been a COLD compile.**
The two steps that make the build-output cache (#1927) worth having are the
restore and the mtime normalisation. On every pull-request run measured —
nine of them, 39533897704, 39534715983, 39534774534, 39536616567,
39536917277, 39536932230, 39537006278, 39538144280 and 39538764502 — the
restore printed

```
Cache not found for input keys: msbuild-v1-...-<pr sha>, msbuild-v1-...-
```

and the normalisation step, which runs only when the restore matched
something, was SKIPPED every time. So nothing was ever restored and the whole
tree was recompiled on every pull request. §6.7's sentence "The PR lane's
build is incremental since #1927's build-output cache landed (it restores the
base branch's `obj/` and rebuilds what changed)" was true of the design and
false of every run: the lane restored nothing.

**And it did not fire on the dev lane either.** The same "Cache not found for
input keys" is what the dev pushes of that day printed — 39541235668,
39543869492, 39548137817, 39548929800, 39550199548 and 39552451465 all had
the normalisation step SKIPPED, and every one of them SAVED an entry
afterwards. The next dev push, 39543869492, could not find the entry the
previous one had saved 18 minutes earlier: same scope, same prefix. The cache
of #1927 had never fired anywhere.

**One cause, and it is measured: the VERSION.** The cache's version is a hash
of the `path` input — upstream, `getCacheVersion(paths, …)` is the SHA-256 of
the path LINES (trimmed, empties dropped) joined with `|`, plus the
compression method and the salt `1.0` — and the restore asks the service for
"the entry with this key AND this version". The comment lines are lines, so
they are in the hash. The restore's path input carried NINE comment lines
that the save's did not, so the two hashed to two different versions and the
restore could never find the entry the save wrote. Replaying that hash over
the two path inputs of the tree that failed (sha `7c7a13e1`): the restore's
14 lines (9 of them comments) hash to `bcff3b41…`, the save's 5 lines hash
to `3dd0d4fb…`; after the fix both inputs are the same 5 lines and both hash
to `3dd0d4fb…`.

The proof in two runs, four minutes apart, in the SAME scope: dispatch
39541292620 saved the entry under the key `…-7c7a13e1` (`Cache saved with
key: msbuild-v1-…-7c7a13e1`), and dispatch 39542435400 then asked for that
EXACT key and the restore still answered

```
Cache not found for input keys: msbuild-v1-…-7c7a13e1, msbuild-v1-…
```

Same key, same scope, other version: the entry was there and the restore
could not see it.

**The scope is not the cause, and the dev lane's own runs say so.** On the dev
push 39543869492 the dev scope already held an entry with the same prefix —
saved 18 minutes earlier by 39541235668 — the run was in that same scope,
and the restore still printed "Cache not found for input keys". What the
pull-request lane adds is that its OWN scope holds nothing (the
pull-request lane never writes), so the entry worth restoring lives in the
base branch's scope — and the exact key is what crosses into it: measured,
run 39545372020, a pull-request run, restored the dev scope's entry
`…-8d82cac1` by its exact key. The step that resolves the entry reads the
REST API, which lists every scope, and names the base scope's newest entry
with this prefix.

**The NuGet cache setup-dotnet restores was found on every pull request** —
because setup-dotnet passes ONE path input to both its restore and its save,
so the two versions agree. That is the whole difference between the two
caches: same mechanism, same action, one pair of path inputs identical and
one pair not.

**After the fix** (runs [39545372020](https://github.com/refusedguy/Harbor-Harness/actions/runs/39545372020)
and [39546195823](https://github.com/refusedguy/Harbor-Harness/actions/runs/39546195823)):
the restore finds the entry for the first time — 20 s and 22 s of download
instead of 0 s — and the normalisation step runs for the first time
(`[normalize-mtimes] base=8d82cac1 base_time=1792556668 changed=2
rebuilt=2 skipped=3820 missing=0 outputs_stamped=15232`).

**And on the tree this branch now sits on** (run
[39556560488](https://github.com/refusedguy/Harbor-Harness/actions/runs/39556560488),
after the rebase onto `dev`): the resolve step looked in `refs/heads/dev`,
named the entry built from dev's HEAD (`…-6f197b60`), the restore took 41 s
and the normalisation step ran again (`[normalize-mtimes] base=6f197b60
base_time=1792562836 changed=5 rebuilt=5 skipped=3833 missing=0
outputs_stamped=15236`).

**And the wall did not get shorter yet, and the reason is the next step.** The
compile measured 265 s on run 39545372020 and 256 s on run 39556560488,
against 215 s on the branch's earlier iteration, before the version fix (run
39541310960, attempt 2, sha 7c7a13e1 — that run's restore still found
nothing, so it compiled the whole tree cold): the cache is restored, and
MSBuild still rebuilds the whole tree. The reason is visible in the tool's
own comment: the analyzer assemblies in the NuGet folder are an INPUT of every
`CoreCompile`, and the compile's own restore extracts them AFTER the outputs
were stamped — so every project's analyzer is newer than its output, and
every project looks stale again. Stamping the outputs ahead of the restore
(the tool stamps with "now", the restore lands a few seconds later) is the
one line that is left. Until that lands, the cache costs its download and
buys nothing: the honest number for this change is "the cache fires", not
"the lane is faster".

**What is left on the critical path after the cache works** (the same step
list, run 39545372020, on the tree that has the pack job and the arch shard):
setup .NET 21 s, restore 20 s, compile 265 s, the pack inputs upload 21 s, the
six shard-slice uploads 38 s. The compile is the term to attack, and §6.7 has
already measured why it is hard: it is analyzer-bound and throughput-bound on
the runner's four cores, so more MSBuild nodes buy nothing and only the
analyzers themselves (or a second runner) do.

#### 6.4.2 The tree IS incremental: the compile rebuilds 3 of 117 projects (#2107)

Issue [#2107](https://github.com/refusedguy/Harbor-Harness/issues/2107) asked
whether the build-output cache pays off, and its own measurement said it does
not: the lane restored the tree and then recompiled **117 of 117** projects,
with the compile at 247-260 s — the cache buying 21 s of download and nothing
else. Measured again on 2026-10-22, on the tree of `dev` @ `bfa1c99d`, the tree
IS incremental: the compile rebuilds **three** projects and costs **36.7 s**.

The count is a fact about the disk, not a reading of the log: a project the
compile rebuilt has an intermediate assembly (`obj/Release/net10.0/<T>.dll`)
NEWER than the moment the compile started, and a project it skipped keeps the
stamping timestamp the restore gave it. The step that measures it is the
`build` job's "DIAGNOSTIC: how many projects the compile rebuilt" in
`.github/workflows/ci.yml`.

| run | lane | build-output cache | NUKE compile | projects rebuilt |
|---|---|---|---:|---:|
| [39723477934](https://github.com/refusedguy/Harbor-Harness/actions/runs/39723477934) | PR `ci/2107-incremental` | **HIT** (`…-bfa1c99d`, 1 841 MB, 36.8 s) | **36.7 s** | **3** of 117 |
| [39719039714](https://github.com/refusedguy/Harbor-Harness/actions/runs/39719039714) | PR `feat/2111-msbuild-switches` | HIT (`…-a019e928`) | 31.2 s | not measured |
| [39721963101](https://github.com/refusedguy/Harbor-Harness/actions/runs/39721963101) | push `dev` | HIT (`…-6e6b0da9`) | 30.9 s | not measured |
| [39718825965](https://github.com/refusedguy/Harbor-Harness/actions/runs/39718825965) | PR `ci/2108-merge-reference-trimmer` | **MISS** — the props hash differs | 190.8 s | 117 (a cold compile) |
| [39721596368](https://github.com/refusedguy/Harbor-Harness/actions/runs/39721596368) | PR `feat/gates-a` | **MISS** — the path list differs | 219.8 s | 117 (a cold compile) |
| [39722582254](https://github.com/refusedguy/Harbor-Harness/actions/runs/39722582254) | manual dispatch | **MISS** — the scope | 183.1 s | 117 (a cold compile) |

**Three reasons the cache misses, and they are three different things.** The
restore step prints "Cache not found for input keys" in all three cases, and
the reason is a different one each time.

1. **The props hash.** The key carries a hash of `Directory.Packages.props`,
   `Directory.Build.props`, `Directory.Build.targets` and `global.json`. A pull
   request that touches one of them has a prefix the base branch has no entry
   for, and the restore finds nothing at all. Measured on run 39718825965: the
   lane's prefix ended in `1de69dbd…`, the base branch's in `61e83d62…`.
   **This miss is correct and cannot be bought back**: `$(MSBuildAllProjects)` —
   every imported `.props`/`.targets`, the props files among them — is an
   INPUT of `CoreCompile` (SDK: `Roslyn/Microsoft.CSharp.Core.targets`), so a
   changed props file is newer than the outputs and every project that imports
   it has to be compiled again. The compile is full either way; what the miss
   costs is the 10-37 s the restore would have cost.
2. **The path list.** The cache's VERSION is a hash of the `path` input, and
   the restore asks the service for "the entry with this key AND this
   version". A branch whose path list differs from the base branch's asks for
   a version the base branch never wrote. Measured on run 39721596368 (the
   branch `feat/gates-a`, whose list carried `!tools/bin/**` and seven comment
   lines that `dev`'s list did not): the resolve step named `dev`'s entry
   `…-6e6b0da9` — the REST API lists every scope and every version — and the
   restore refused it. **Fixed here**: the list is one env var
   (`HARBOR_BUILD_OUTPUT_CACHE_PATHS`) that the restore step and the save step
   both read, so there is no second copy to drift, and the comments live in the
   YAML and not inside the value (a comment inside the value is a LINE of the
   hash, so editing one used to change the version and cost the lane its
   cache). The hash of the four lines the var holds is
   `c59a4786cc2123d53e9d1c95486e32a211f9dcf3885f53318b7aedde237d9947`
   (`sha256` of the lines joined with `|`, plus `zstd-without-long` and the
   salt `1.0`), and that is the `version` the REST API reports for every entry
   `dev` has written since #2117.
3. **The scope.** The cache service serves a run the entries of its own
   branch, of the branches below it, and — for a pull request — of the base
   branch. A manual dispatch on a feature branch gets none of `dev`'s
   entries, and the run the issue quotes (39658451546) is such a dispatch: its
   resolve step found `dev`'s entry through the REST API and the restore could
   not see it. Nothing in the workflow can change that; the scope is the
   service's rule.

**What the issue prescribed, and what the measurement says.** The issue asked
for the restore to be moved before the stamping. It is already where it has to
be: the restore inside the compile is a NO-OP — it does not rewrite the
NuGet-generated files, and those files (`obj/<T>.csproj.nuget.g.props`) are in
`$(MSBuildAllProjects)`, so a rewrite would make every project look stale
again. The measurement says they are not rewritten: three projects rebuilt,
not 117. The issue's other candidate — the `.pdb` — is not an output of
`CoreCompile` in Release at all (`Directory.Build.props` sets
`DebugType=none` there, so `_DebugSymbolsProduced` is false and the `.pdb`
leaves the target's output list), which is why excluding it from the payload
changed nothing.

**And the cause the merged diagnostic reported was the diagnostic's own.**
The step merged with #2123 asks MSBuild for its verdict on one project, and it
answered
"Input file `…GeneratedMSBuildEditorConfig.editorconfig` is newer than output
file `…xml`". The step builds that project on its own, and a project-level
build has no `$(SolutionDir)`, so the generated editorconfig gets
`build_property.SolutionDir = *Undefined*` instead of the solution build's
path — the content differs, the SDK rewrites the file (it writes only when the
content differs), and the rewrite makes it newer than the outputs. Measured on
run 39723477934, the diff was exactly that one line. The diagnostic therefore
rebuilt the very project it was measuring and named a cause the lane does not
have; the step now passes `-p:SolutionDir="$GITHUB_WORKSPACE/"`, so its
verdict is the lane's own. Measured on run 39725284841, after that fix: the
editorconfig's hash is the same before and after the diagnostic's own build
(`e68c5f62…`), the diff is empty, and MSBuild answers **"Skipping target
"CoreCompile" because all output files are up-to-date with respect to the
input files."** — the tree is incremental, said by MSBuild itself.

#### 6.4.3 The cache hits in 78 % of the pull-request runs, and every miss has a name

The cache does not "sometimes work": it hits on **14 of the 18** pull-request
runs measured on 2026-10-22, and each of the four misses has a cause that is
in the pull request's own diff. The table is every pull-request run of
`.github/workflows/ci.yml` in the last 60 runs that had a build job; the
compile is the NUKE `Compile` phase and the wall is the `build` job's.

| run | lane | cache | why | NUKE compile | `build` job |
|---|---|---|---|---:|---:|
| [39725284841](https://github.com/refusedguy/Harbor-Harness/actions/runs/39725284841) | PR `ci/2107-incremental` | HIT | — | 29.6 s | 185 s |
| [39724322981](https://github.com/refusedguy/Harbor-Harness/actions/runs/39724322981) | PR `perf/arch-parser` | HIT | — | 52.3 s | 210 s |
| [39723562597](https://github.com/refusedguy/Harbor-Harness/actions/runs/39723562597) | PR `fix/2061b` | HIT | — | 213.6 s | 389 s |
| [39723477934](https://github.com/refusedguy/Harbor-Harness/actions/runs/39723477934) | PR `ci/2107-incremental` | HIT | — | 36.7 s | 180 s |
| [39723469087](https://github.com/refusedguy/Harbor-Harness/actions/runs/39723469087) | PR `perf/arch-parser` | HIT | — | 55.2 s | 217 s |
| [39723207604](https://github.com/refusedguy/Harbor-Harness/actions/runs/39723207604) | PR `ci/2107-incremental` | HIT | — | 29.6 s | 151 s |
| [39722755948](https://github.com/refusedguy/Harbor-Harness/actions/runs/39722755948) | PR `perf/arch-parser` | HIT | — | 60.3 s | 230 s |
| [39721927302](https://github.com/refusedguy/Harbor-Harness/actions/runs/39721927302) | PR `feat/gates-d-5` | HIT | — | 65.4 s | 251 s |
| [39721911469](https://github.com/refusedguy/Harbor-Harness/actions/runs/39721911469) | PR `perf/arch-parser` | HIT | — | 55.4 s | 219 s |
| [39721827406](https://github.com/refusedguy/Harbor-Harness/actions/runs/39721827406) | PR `perf/arch-parser` | HIT | — | (not measured) | 80 s |
| [39721223746](https://github.com/refusedguy/Harbor-Harness/actions/runs/39721223746) | PR `feat/gates-d-6` | HIT | — | 57.9 s | 207 s |
| [39721154583](https://github.com/refusedguy/Harbor-Harness/actions/runs/39721154583) | PR `perf/arch-parser` | HIT | — | 53.2 s | 212 s |
| [39720669054](https://github.com/refusedguy/Harbor-Harness/actions/runs/39720669054) | PR `fix/2119-event-topology` | HIT | — | 39.0 s | 207 s |
| [39720312442](https://github.com/refusedguy/Harbor-Harness/actions/runs/39720312442) | PR `fix/2119-event-topology` | HIT | — | 32.2 s | 199 s |
| [39724256147](https://github.com/refusedguy/Harbor-Harness/actions/runs/39724256147) | PR `feat/gates-a` | **MISS** | the path list | 147.9 s | 241 s |
| [39722793701](https://github.com/refusedguy/Harbor-Harness/actions/runs/39722793701) | PR `feat/gates-a` | **MISS** | the path list | 223.0 s | 344 s |
| [39721596368](https://github.com/refusedguy/Harbor-Harness/actions/runs/39721596368) | PR `feat/gates-a` | **MISS** | the path list | 219.8 s | 346 s |
| [39721996221](https://github.com/refusedguy/Harbor-Harness/actions/runs/39721996221) | PR `ci/2108-merge-reference-trimmer` | **MISS** | the props hash | 221.5 s | 356 s |

**14 hits, 4 misses: 78 %.** The three `feat/gates-a` runs are one branch (it
was pushed three times), so per branch the sample is 8 branches that hit on
every run and 2 that missed on every run. The three dev pushes of the same
window hit as well (39723714473, 39724342525, 39725519389).

**WHAT THE KEY IS, AND WHAT A HIT DEPENDS ON.** The key is

```
msbuild-v1-<os>-<arch>-sdk<sdk>-nuget<packages path>-<props hash>-<sha>
```

and the parts that decide a hit are:

| part | what it is | a miss when |
|---|---|---|
| `<props hash>` | `hashFiles` over `Directory.Packages.props`, `Directory.Build.props`, `Directory.Build.targets`, `global.json` | the pull request touches one of the four — the prefix names an entry no branch has (run 39721996221: the lane's prefix ended in `1de69dbd…`, the base branch's in `61e83d62…`) |
| the VERSION | the SHA-256 of the cache action's `path` input: the lines joined with a pipe, plus the compression method and the salt `1.0` | the pull request's `path` list differs from the base branch's — the service is asked for "the entry with this key AND this version", and the base branch never wrote that version (runs 39721596368, 39722793701, 39724256147) |
| the scope | the service serves a run the entries of its own branch, of the branches below it and — for a pull request — of the base branch | the run is not a pull request and not a push to `dev`: a manual dispatch on a feature branch sees none of `dev`'s entries (run 39658451546) |

**THERE IS NO RACE IN IT.** The key is not computed from the pull request's
commit: the resolve step names the base branch's newest entry with the prefix
(it sorts the REST API's list by `created_at` and takes the last), and the
`restore-keys` fallback asks the service for "the newest entry with this
prefix" among the scopes the run can read. So a base branch whose newest push
has not saved its entry yet costs the lane FRESHNESS, not the hit — the
stamper's diff covers the delta, and an entry that is one push older is still
a valid base.

**A HIT DOES NOT MEAN A FAST COMPILE, AND THAT IS A FOURTH, NAMED CASE.** Run
39723562597 hit the cache and still compiled in 213.6 s. Its diff against the
base carries `BannedSymbols.txt` and the analyzer that reads it: the analyzer
is rebuilt, its output assembly is an INPUT of every `CoreCompile`, so every
project is out of date and the whole tree compiles again. Same shape as a
props change: the cache hit, and the tree still full.

**WHAT A MISS COSTS.** The `build` job's wall, same lane shape: 185 s on a
hit (run 39725284841) and 297 s on a miss (run 39725420279) — **112 s**, or
1.9 runner-minutes. The whole pull-request lane costs 15.0 runner-minutes on a
hit and 17.1 on a miss (the sum of the job durations, measured on those two
runs), of which the `build` job is 3.1 and 5.0 minutes and the
`unused references (ReferenceTrimmer)` job — a SECOND, full compile, because
the analyzer has to run in every project — is 4.1 minutes either way. At the
measured 78 % hit rate the misses cost 0.22 × 112 s = **25 s per lane**, and a
lane that hit every time would cost 15.0 runner-minutes instead of 15.4.

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
it is generated by `harbor-gates gen-e2e-shards` and graded by
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
the compiled `measure-build-cpu` gate runs a command and measures it,
the compiled `aggregate-build-vcpu` gate turns the runs into the table below, and
the compiled `measure-project-graph` gate parses the dependency graph.

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
whole tree. The PR lane's build was NOT incremental while this section was
written: the build-output cache never fired (the restore found nothing on
every pull-request run, and on every dev push of that day too — §6.4.1 has
the runs and the cause), so the PR lane compiled the whole tree too, and the
PR lane's wall time IS the number below.

**That is still true after the cache was fixed.** The cache now fires on the
pull-request lane (the restore finds the base branch's entry and the
normalisation step runs), but the PR lane's compile is STILL a full rebuild:
the restored outputs are there, and the analyzer assemblies in the NuGet
folder are newer than every one of them, so MSBuild rebuilds the tree either
way (§6.4.1 names the one line that is left). The number below therefore
still describes the pull-request lane.

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
(the compiled `measure-project-graph` gate, on this branch's tree, re-measured on
2026-10-21 after the rebase onto `dev`): 112 projects, 492 edges, depth 15,
widest level 22 projects, and 9 of its 16 levels are wide enough for four
compilers.

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
`-p:MaxCpuCount=0` (what `build/_build/Targets/CompileTarget.cs` used to
pass) was a no-op for the same reason: `dotnet build` is parallel on its own,
which is why arm A, arm B and the CI-exact calibration all read 3.3..3.6 busy
cores. #2111 replaced that line with the real switches — see §6.7.2.

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

The three tools are the reusable instrument (the compiled `measure-build-cpu` gate
runs a command and meters it, the compiled `aggregate-build-vcpu` gate turns the runs
into the table, the compiled `measure-project-graph` gate parses the graph), and the
nightly lane's analysis step can read the same measurements instead of
growing a second parser.

### 6.7.1 The analyzer profile: what each family costs the compile

**The instrument.** The analyzer-cost harness
(`.github/workflows/meas-analyzer-cost.yml`) builds the Release compile of
`Harbor.slnx` twice in one job — the baseline and its own arm — and the
compiler's own `-p:ReportAnalyzer=true` report says, for every
compilation, the elapsed time per analyzer assembly. The numbers below are
that report, summed over the 117 compilations of the build; the reader is
the compiled `aggregate-analyzer-cost` gate, and its table is the artifact
`analyzer-cost-table`.

**The two runs.** The before/after pair of issue #2109, on the same day,
on the same runner image (`ubuntu-24.04`, 4 vCPU, .NET SDK 10.0.402):

| run | branch | tree | `narrowed-report` arm |
|-----|--------|------|----------------------|
| [39633281684](https://github.com/refusedguy/Harbor-Harness/actions/runs/39633281684) | `ci/2109-baseline` | `dev` as it was | 117 compilations, 841.6 analyzer-seconds |
| [39633295157](https://github.com/refusedguy/Harbor-Harness/actions/runs/39633295157) | `ci/2109-profile` | with the ILLink analyzer and the vs-threading family removed | 117 compilations, 518.3 analyzer-seconds |

#### The profile, family by family

| family | analyzer seconds | share | on by default? | decision |
|--------|------------------|-------|----------------|----------|
| `SonarAnalyzer.CSharp` | 249.5 | 48.1 % | `tests/` only | KEPT — the only family whose 324 findings the repository reads, and all of them are in the test tree |
| `DependencyInjection.Lifetime.Analyzers` | 67.8 | 13.1 % | `src/` + `apps/` | KEPT — DI003/DI015/DI017/DI019 are errors on the composition root's registrations |
| `ILLink.RoslynAnalyzer` (the SDK's own) | 48.2 | 9.3 % | 7 projects | NARROWED — 302.2 s in 105 projects before |
| `Meziantou.Analyzer` | 48.0 | 9.3 % | solution-wide | KEPT |
| `Microsoft.CodeAnalysis.NetAnalyzers` | 17.0 | 3.3 % | solution-wide | KEPT — the base layer |
| `Roslynator.Analyzers` | 13.0 | 2.5 % | solution-wide | KEPT |
| `AsyncFixer` | 11.7 | 2.3 % | `src/` + `apps/` | KEPT — all five rules are `suggestion`, so it cannot fail a build |
| `Microsoft.CodeAnalysis.BannedApiAnalyzers` | 10.7 | 2.1 % | solution-wide | KEPT — RS0030 is a build error over `BannedSymbols.txt` |
| `Harbor.Arch.Analyzers` (the repository's own) | 8.8 | 1.7 % | `src/` + `apps/` | KEPT — HARCH001/HARCH002, in the build, over every product project |
| `ReflectionAnalyzers` | 5.9 | 1.1 % | solution-wide | KEPT |
| `Microsoft.CodeAnalysis.PublicApiAnalyzers` | 2.7 | 0.5 % | two frozen assemblies | KEPT |
| `DotNetProjectFile.Analyzers` | 2.1 | 0.4 % | solution-wide | KEPT |
| `Microsoft.VisualStudio.Threading.Analyzers` | **0** | **0 %** | **REMOVED** | REMOVED — 114.4 s, of which 93.6 s was VSTHRD010, a rule at `severity = none` |
| **everything together** | **518.3** | | | **−323.3 s against 841.6 s** |

The source generators and the SDK's own analyzer assemblies make up the
rest of the 518.3 s (`TUnit.Core.SourceGenerator` 6.3,
`CommunityToolkit.Mvvm.SourceGenerators` 4.1, `Harbor.CodeGen` 1.4, and
the `Microsoft.Interop.*` generators 3.3 among them).

#### The before/after, and the two things that make it hard to read

**The runner is not the same machine twice.** The analyzer report is CPU
time, and the second run's runner was the slower one: the 22 families
this change did not touch came out **6.3 % slower** there (the median of
their per-family ratios; the spread is 0.59x..1.30x). Normalized by that
factor:

| | before | after | delta |
|---|---:|---:|---:|
| `ILLink.RoslynAnalyzer` | 302.2 s (105 projects) | 45.3 s (7 projects) | **−85 %** |
| `Microsoft.VisualStudio.Threading.Analyzers` | 114.4 s (60 projects) | 0 s | **−100 %** |
| everything together | 841.6 s | 487.4 s | **−354.2 s (−42.1 %)** |

**The wall cannot resolve an effect of this size, and the harness says so
in its own table.** Every paired arm — the same tree, twice, in one job,
on the same runner — came out inside the noise floor, which the control
arms (the baseline against itself) put at **28.0 s** in the first run and
**28.4 s** in the second:

| arm | before run: difference (s) | after run: difference (s) | the family it switches off |
|------|--------------------------:|-------------------------:|----------------------------|
| `no-illink` | **+1.4** | −14.0 | the SDK's ILLink analyzer — 302.2 s of analyzer time |
| `no-vs-threading` | **+8.7** | −2.6 | 114.4 s of analyzer time, 93.6 s of it in one silenced rule |
| `no-roslynator` | −6.7 | **+46.1** | 12.3 s of analyzer time |
| `no-publicapi` | +19.2 | +25.2 | 3.3 s of analyzer time |
| `no-sonar` | +9.6 | +4.6 | 232.4 s of analyzer time |

A 3-second family reads +19.2 s and +25.2 s, a 232-second family reads
+9.6 s, and the largest arm in either run is a 12-second family at
+46.1 s. **The wall-time arms measure the runner, not the analyzer.** The
controlled number is the compiler's own report, and it is what the table
above is read from.

#### What the two removed families were

**`ILLink.RoslynAnalyzer` (302.2 s → 48.2 s).** The SDK's own AOT/trim
analyzer, in 105 of the tree's 117 compilations, because the
repository-root `Directory.Build.props` set `PublishSingleFile=true`
repo-wide and the SDK pulls the ILLink pack into every project that
enables the single-file analyzer. The property now lives in
`apps/Harbor.App.Cli`, and the analyzer runs where it has something to
say: the CLI's own compilation, the six `IsAotCompatible=true` projects,
and the `aot-publish` job (`-p:EnableSingleFileAnalyzer=true`), which is
where the product tree's `IL3000` coverage went. The mechanism and the
proof are in [docs/ANALYZERS.md §The ILLink analyzer](./ANALYZERS.md#the-illink-analyzer).

**`Microsoft.VisualStudio.Threading.Analyzers` (114.4 s → 0 s).** 93.6 s
of the 114.4 s — 82 % — is `VSTHRD010MainThreadUsageAnalyzer`, the rule
`.editorconfig` sets to `severity = none`. A silenced rule still runs: the
analyzer is invoked, the diagnostic is dropped, and the time is paid. The
only rule the repository asked for is VSTHRD110, at `suggestion`, which
`TreatWarningsAsErrors` does not promote — so no rule of the family could
fail a build. The gate that decides "is this an unobserved Task?" is
HARCH003, a house analyzer that resolves the TYPE rather than matching a
call's spelling, and it is a Warning, so it fails the build where
VSTHRD110 only suggested.

#### The zero-warning gate did not get weaker

The ILLink analyzer's only findings in the ordinary build were four
`IL3000` warnings — `Assembly.Location` in
`tests/Harbor.CodeGen.Tests/ObservedGeneratorDriverTests.cs:233`,
`tests/Harbor.Plugins.Runtime.Tests/Compilation/PluginReferenceContractTests.cs:364`,
`tests/Harbor.Plugins.Runtime.Tests/Security/SandboxAlcTests.cs:92` and
`tests/Harbor.TestKit.Generators.Tests/FakeGeneratorDriverTests.cs:164` —
all under `tests/`, where `TreatWarningsAsErrors` is off, so none of them
could fail a build. The product tree's own `IL3000`
site is a `#pragma warning disable IL3000` in
`src/Harbor.Plugins.Compilation/PluginAssemblyReferences.cs`, and the
proof that the gate moved rather than disappeared is a run with that
pragma removed:

| run | tree | the band |
|-----|------|----------|
| [39637090212](https://github.com/refusedguy/Harbor-Harness/actions/runs/39637090212) | before | the **build** job is red — the IL3000 warning is an error under `TreatWarningsAsErrors=true` |
| [39637286644](https://github.com/refusedguy/Harbor-Harness/actions/runs/39637286644) | after | the **build** job is green and the **aot-publish** job is red — the check is in the job that owns it |
### 6.7.2 The real MSBuild switches: `/maxcpucount`, `/nodeReuse`, `/graphBuild`

**What the NUKE Compile target passed, and why it was nothing.**
`build/_build/Targets/CompileTarget.cs` ran

```
dotnet build Harbor.slnx -c Release --no-restore -p:MaxCpuCount=0 -p:ContinuousIntegrationBuild=true
```

`-p:MaxCpuCount=0` is a PROPERTY. MSBuild reads the node count as the
SWITCH `-maxcpucount`, and the `MaxCpuCount` property appears in no
MSBuild target and in no global-property lookup — §6.7 measured that
property as indistinguishable from the baseline (wall ratio 1.08). The
line read like "use all CPUs" and did nothing at all. Worse, the
"0 means all CPUs" spelling is not even a legal value: MSBuild's
`ProcessMaxCPUCountSwitch` throws `InvalidMaxCPUCountValueOutsideRange`
for anything outside 1..1024.

**What replaced it.** `dotnet build` is `dotnet msbuild -restore -t:Build`
plus the SDK's own defaults, and the node count is one of the switches
only MSBuild's own command line carries. Going through `dotnet msbuild`
is what makes the switches reachable at all:

```
dotnet msbuild Harbor.slnx -t:Build -p:Configuration=Release \
  -p:ContinuousIntegrationBuild=true -maxcpucount:4 -nodeReuse:false
```

**The measurement** (CI run 39634501061, `meas-build-vcpu.yml`, three
replicates per arm, the same runner: `ubuntu-24.04` image
`ubuntu24/20261018.339.1`, 4 vCPU, .NET SDK 10.0.402). The architecture
gate is ON in this harness, so its duration is the runner's speed proxy
and it is subtracted in the "compile phase" column.

| arm | the command | n | wall (s) | median | compile phase (wall − gate), median | `Csc` task time, median | speed-normalized wall ratio |
|-----|-------------|--:|----------|--------|--------------------------------------|--------------------------|-----------------------------|
| A | `dotnet build … -p:MaxCpuCount=0` (what CI ran before #2111) | 3 | 253.0..409.6 | 319.4 | 202.6 s | 266.2 s | 1.00 |
| E | `dotnet msbuild … -maxcpucount:4 -nodeReuse:false` | 3 | 237.4..376.6 | 304.2 | **189.7 s** | 265.7 s | **0.9** |
| F | E, plus `-graphBuild` | 3 | 282.7..447.2 | 412.1 | **253.9 s** | **858.2 s** | 1.0 |

**Arm E is what the build now runs.** The same build, the real switches:
the compile phase is 6 % shorter and the speed-normalized wall ratio is
0.9. The `Csc` task time is the same (265.7 s against 266.2 s) — the
wall difference is in how the work is scheduled, not in how much of it
there is.

**Arm F is NOT adopted, and the reason is measured.** `/graphBuild`
WORKS on this solution — 118 compilations, exit 0, the same project set,
so a `.slnx` with a solution-level metaproject is not a shape the graph
build refuses. But it costs **1.3x the wall** (412.1 s against 304.2 s
at the median) and **3.2x the compiler's own task time** (858.2 s
against 265.7 s): the graph is constructed once, but every project is
then evaluated in a graph node, and the analyzers run again. The arm
stays in the harness, so the number can be re-read when the tree's shape
changes.

**Node reuse goes off on CI, the way dotnet/arcade does it.** The
`build` job sets `MSBUILDDISABLENODEREUSE=1`, the environment variable
MSBuild's `ProcessNodeReuseSwitch` reads, and the NUKE target passes
`/nodeReuse:false`, which is the same decision as a switch (the switch
wins over the environment variable). The reason is not speed: a reused
node carries the previous build's state — its evaluation caches, its
loaded assemblies — so a build that reuses a node is not the same build
as a build that does not. On CI there is nothing to reuse: this job
compiles once.

### 6.8 Coverage per project (#1753) ✅ measured in the nightly lane

Measured on the nightly run [39627786424](https://github.com/refusedguy/Harbor-Harness/actions/runs/39627786424), branch `dev` at `259d0a5d`, 2026-10-21 — the first run in which the lane got all the way to the measurement. The lane is the `coverage` job of `.github/workflows/nightly.yml`; the reader is [`tools/gates/coverage-report/`](../tools/gates/coverage-report/), the registry is [`tools/coverage-registry.txt`](../tools/coverage-registry.txt), and the gate is [`tools/gates/check-coverage-registry/`](../tools/gates/check-coverage-registry/). All twenty suites of the lane passed and every report parsed, so the measurement is COMPLETE, not partial. The tree has moved since that run; §6.8.1 says by how much and what the gate said about the new tree.

**WHAT THIS NUMBER IS NOT.** There is no global percentage here, on purpose. The number the lane used to publish was a sum over the per-suite columns, and those columns OVERLAP on the shared `src/` assemblies — the same line is counted once per suite that touched it — so the sum was never the coverage of anything, and the report said so in five words ("the TOTAL is indicative"). What is below is per project and per file, with the scope written down.

**THE SCOPE.** The twenty core+platform unit suites. The ui, e2e and load suites are not part of the lane: they are slow, they need a display, and their flakes (#423) would make the number unreadable. Two of the product areas therefore sit OUTSIDE the measurement, and the registry says so per file instead of the number averaging them away.

**THE FOUR STATES OF A FILE.** The engine is the oracle, not a guess: it instruments whole modules and reports every file of them that has something to instrument.

| state | what it means | files |
|---|---|---|
| covered | at least one line was hit | 554 |
| never ran | the engine saw the file and not one of its lines ran | 378 |
| not loaded | the file's project is not in this lane at all | 210 |
| no code to cover | the engine reported nothing for the file: an interface, an enum, a global-using file, a source-generated context | 186 |

**THE PRODUCT, PER PROJECT** (1328 files under `src/` and `apps/`)

| project | files | covered | never ran | not loaded | no code to cover |
|---|---|---|---|---|---|
| `src/Harbor.Tui.CellForge` | 113 | 30 | 82 | 0 | 1 |
| `src/Harbor.Tui.CellForge.Engine` | 91 | 4 | 75 | 0 | 12 |
| `src/Harbor.Ui.Framework.Rendering` | 50 | 7 | 32 | 0 | 11 |
| `src/Harbor.Ui.Framework.Services` | 32 | 3 | 23 | 0 | 6 |
| `src/Harbor.Ui.Framework.State` | 51 | 23 | 22 | 0 | 6 |
| `src/Harbor.Desktop.ViewModels` | 20 | 0 | 18 | 0 | 2 |
| `src/Harbor.Terminal.Abstractions` | 27 | 5 | 16 | 0 | 6 |
| `src/Harbor.Ui.Framework.Projection` | 19 | 2 | 11 | 0 | 6 |
| `src/Harbor.Ui.Framework.Abstractions` | 18 | 0 | 10 | 0 | 8 |
| `src/Harbor.CodeGen` | 9 | 0 | 9 | 0 | 0 |
| `src/Harbor.Ui.Framework.ViewModels` | 10 | 1 | 9 | 0 | 0 |
| `src/Harbor.Tui.CellForge.Syntax` | 9 | 0 | 8 | 0 | 1 |
| `src/Harbor.Ui.Framework.Sessions` | 15 | 2 | 8 | 0 | 5 |
| `src/Harbor.Desktop.Shared` | 9 | 0 | 7 | 0 | 2 |
| `src/Harbor.Abstractions` | 71 | 41 | 5 | 0 | 25 |
| `src/Harbor.Abstractions.Contracts` | 48 | 38 | 5 | 0 | 5 |
| `src/Harbor.DesignSystem` | 14 | 4 | 5 | 0 | 5 |
| `src/Harbor.Desktop.Abstractions` | 11 | 1 | 5 | 0 | 5 |
| `src/Harbor.Plugins.Host` | 6 | 2 | 4 | 0 | 0 |
| `src/Harbor.Terminal.Pty` | 4 | 0 | 4 | 0 | 0 |
| `src/Harbor.Desktop.Animations` | 5 | 0 | 3 | 0 | 2 |
| `src/Harbor.Extensions` | 5 | 2 | 2 | 0 | 1 |
| `src/Harbor.Hosting` | 37 | 34 | 2 | 0 | 1 |
| `src/Harbor.Tools.Builtin` | 77 | 65 | 2 | 0 | 10 |
| `src/Harbor.Tui.AnsiPlain` | 9 | 3 | 2 | 0 | 4 |
| `src/Harbor.Application` | 32 | 25 | 1 | 0 | 6 |
| `src/Harbor.Logging` | 1 | 0 | 1 | 0 | 0 |
| `src/Harbor.Plugins.Abstractions` | 25 | 11 | 1 | 0 | 13 |
| `src/Harbor.Plugins.Hosting` | 13 | 11 | 1 | 0 | 1 |
| `src/Harbor.Plugins.Storage` | 9 | 7 | 1 | 0 | 1 |
| `src/Harbor.Telemetry.Otlp` | 1 | 0 | 1 | 0 | 0 |
| `src/Harbor.Transport.Remote` | 5 | 3 | 1 | 0 | 1 |
| `src/Harbor.Tui.CellForge.Prompts` | 3 | 1 | 1 | 0 | 1 |
| `src/Harbor.Tui.Notifications` | 1 | 0 | 1 | 0 | 0 |
| `apps/Harbor.App.Avalonia` | 93 | 0 | 0 | 93 | 0 |
| `apps/Harbor.App.Cli` | 117 | 0 | 0 | 117 | 0 |
| `src/Harbor.AgentRuntime` | 36 | 32 | 0 | 0 | 4 |
| `src/Harbor.Arch.Analyzers` | 2 | 2 | 0 | 0 | 0 |
| `src/Harbor.ChangeTracking` | 12 | 11 | 0 | 0 | 1 |
| `src/Harbor.Diagnostics.Abstractions` | 3 | 2 | 0 | 0 | 1 |
| `src/Harbor.Hooks` | 6 | 3 | 0 | 0 | 3 |
| `src/Harbor.Ipc.Abstractions` | 20 | 15 | 0 | 0 | 5 |
| `src/Harbor.Ipc.Client` | 12 | 10 | 0 | 0 | 2 |
| `src/Harbor.Ipc.InProcess` | 2 | 2 | 0 | 0 | 0 |
| `src/Harbor.Ipc.Server` | 14 | 13 | 0 | 0 | 1 |
| `src/Harbor.Lsp` | 4 | 4 | 0 | 0 | 0 |
| `src/Harbor.Onboarding` | 4 | 3 | 0 | 0 | 1 |
| `src/Harbor.Permissions` | 8 | 6 | 0 | 0 | 2 |
| `src/Harbor.Plugins.Compilation` | 6 | 5 | 0 | 0 | 1 |
| `src/Harbor.Plugins.Instantiation` | 3 | 2 | 0 | 0 | 1 |
| `src/Harbor.Plugins.Registration` | 8 | 7 | 0 | 0 | 1 |
| `src/Harbor.Plugins.Runtime` | 3 | 2 | 0 | 0 | 1 |
| `src/Harbor.PolicyConfig` | 14 | 10 | 0 | 0 | 4 |
| `src/Harbor.Providers.Anthropic` | 3 | 3 | 0 | 0 | 0 |
| `src/Harbor.Providers.Ollama` | 1 | 1 | 0 | 0 | 0 |
| `src/Harbor.Providers.OpenAI` | 3 | 3 | 0 | 0 | 0 |
| `src/Harbor.Providers.OpenAiCompatible` | 5 | 3 | 0 | 0 | 2 |
| `src/Harbor.Providers.Shared` | 5 | 5 | 0 | 0 | 0 |
| `src/Harbor.Registries` | 14 | 12 | 0 | 0 | 2 |
| `src/Harbor.SessionContext` | 19 | 16 | 0 | 0 | 3 |
| `src/Harbor.Storage.Jsonl` | 35 | 32 | 0 | 0 | 3 |
| `src/Harbor.Storage.Memory` | 3 | 3 | 0 | 0 | 0 |
| `src/Harbor.Storage.Shared` | 4 | 4 | 0 | 0 | 0 |
| `src/Harbor.Storage.Sqlite` | 9 | 8 | 0 | 0 | 1 |
| `src/Harbor.Telemetry.Core` | 10 | 10 | 0 | 0 | 0 |
| **total** | **1328** | **554** | **378** | **210** | **186** |

**THE LINE COUNT, for the files the engine instrumented.** 67 426 product lines, 31 249 of them hit (46.3 %). The 24 415 lines of the 378 "never ran" files are the other half of that gap, and they are the deletion candidates: a file nobody ever ran is the strongest form of the "an uncovered line is often dead code" rule (#772, #755).

**THE SHARE OF CONSCIOUSLY UNCOVERED CODE.** 588 of the 1328 product files (44.3 %) are holes of this measurement, and every one of them is in the registry with a reason: 378 file rows for the files the measurement saw and never ran, and 2 project rows (`apps/Harbor.App.Cli/`, `apps/Harbor.App.Avalonia/`) for the two projects the lane does not load at all. The registry is a RATCHET in both directions: a file that stops being covered has to have a row added, and a row whose file got covered has to have it deleted — the gate fails on either.

#### 6.8.1 The registry against the measurement (#2064) ✅ the first reconciliation

**THE STATE THE ISSUE FOUND.** The registry was written against run 39528039639 (branch `ci/1753-per-file-coverage` at `c480a98f`) and never checked against a measurement again. Since then the tree grew by 78 product files, and five of the new ones landed outside the lane's reach: the tool-schema generator (#2063), the [Observed] generator's diagnostics (#2072), the plugin host's config source (#2048) and its inventory publisher (#2076), and the `[ToolParam]` attribute (#2063). Meanwhile `src/Harbor.Abstractions/Sessions/ISessionStore.cs` got covered. The registry was wrong in both directions, and it stayed wrong because NO measurement had been taken since — the lane died before it measured, first on a broken build (`CS0234` in `Harbor.Application.Tests`), then on a missing `Coverage/` directory, so the gate read "no coverage directory" instead of a verdict.

**THE RECONCILIATION, against run 39627786424** (`dev` at `259d0a5d`; 20 suites, all passed, all reports parsed, so the measurement is complete).

| | before | after |
|---|---|---|
| rows in the registry | 376 | 380 |
| file rows / project rows | 374 / 2 | 378 / 2 |
| holes in the measurement (files the lane cannot account for) | 588 | 588 |
| holes the registry accepted | 583 | 588 |
| holes NO row accepted | 5 | 0 |
| rows that outlived their file (the file got covered) | 1 | 0 |
| files the registry names and the tree does not have | 0 | 0 |
| **the gate's verdict** | **6 findings, exit 1** | **0 findings, exit 0** |

**THE SIX FINDINGS** the gate printed on the dev tree, and what each one became:

| finding | file | what it is | what was done |
|---|---|---|---|
| `COV-UNCOVERED` | `src/Harbor.CodeGen/ToolSchemaGenerator.cs` | the generator runs inside the compiler at build time | row added |
| `COV-UNCOVERED` | `src/Harbor.CodeGen/ObservedDiagnostics.cs` | the descriptors of the [Observed] generator; same reason | row added |
| `COV-UNCOVERED` | `src/Harbor.Plugins.Host/ConfigFilePluginConfigSource.cs` | read by the plugin host at its startup; no suite of the lane starts the host | row added |
| `COV-UNCOVERED` | `src/Harbor.Plugins.Host/PluginInventoryPublisher.cs` | called by the host after its load and after every activation pass | row added |
| `COV-UNCOVERED` | `src/Harbor.Tools.Builtin/ToolParamAttribute.cs` | read by the schema generator inside the compiler; a test process only carries its metadata | row added |
| `COV-ROW-STALE` | `src/Harbor.Abstractions/Sessions/ISessionStore.cs` | the file is covered now, so the row outlived its reason | row deleted |

Each new row carries a reason of at least 40 characters and an owner, like every other row: the registry is a list of conscious holes, not a list of excuses.

**THE GATE FALLS WHEN THE TWO DISAGREE — the negative check.** A gate that cannot fail is not a gate, so the divergence is planted on purpose and the lane is run again with it. The planted defect is the row that was deleted above (`src/Harbor.Abstractions/Sessions/ISessionStore.cs`, a file the measurement says is covered): the lane must report `COV-ROW-STALE` and fail.

| run | what it measured | the gate's verdict |
|---|---|---|
| [39627786424](https://github.com/refusedguy/Harbor-Harness/actions/runs/39627786424) | the tree at `dev` `259d0a5d`, BEFORE the fix | **6 findings, exit 1** — 5 `COV-UNCOVERED` and 1 `COV-ROW-STALE`; the `coverage` job was red |
| [39632794079](https://github.com/refusedguy/Harbor-Harness/actions/runs/39632794079) | the branch with the fix (`fix/2064-coverage-registry`, `a6eccccd`) | **0 findings, exit 0** — `product files: 1328 covered: 554 uncovered: 588 in the registry: 588 registry rows: 380`; the `coverage` job, the `gate` job and the `measurement` job are all green |
| [39633963169](https://github.com/refusedguy/Harbor-Harness/actions/runs/39633963169) | the same branch with ONE row planted back — the row for the file that got covered | **1 finding, exit 1** — `COV-ROW-STALE: the file is covered now, so the row has outlived its reason and has to go (src/Harbor.Abstractions/Sessions/ISessionStore.cs)`; the `coverage` job is red, the other two jobs are green |
| [39719902323](https://github.com/refusedguy/Harbor-Harness/actions/runs/39719902323) | the merged tree — `dev` at `a019e928` plus this branch — with the registry as it stands | **0 findings, exit 0** — `product files: 1329 covered: 555 uncovered: 588 in the registry: 588 registry rows: 380`; the `gate` job, the `coverage` job and the `measurement` job are all green |

The third run is the negative control, and it is a CI run of the whole lane: the same tree, the same measurement (1328 / 554 / 588), and a registry with one row too many. The gate saw it and failed the job. The gate's own `--self-test` proves the same thing without a runner: it plants a file that stopped being covered, a row that is no longer needed, an empty measurement, an unreadable report, a row for a file that is gone, and a partial measurement, and it asserts that each one produces its finding.

**WHAT CAME AFTER THE MEASUREMENT.** The tree grew by ONE product file after that run — `src/Harbor.Arch.Analyzers/HttpClientCreationAnalyzer.cs` (#2148) — and the lane's arch suite drives it in process (`tests/Harbor.Architecture.Tests/Roslyn/HttpClientCreationAnalyzerTests.cs`), so it is covered and no row is due for it. No product file was deleted or moved since, so no row names a file the tree does not have. Run 39719902323 measured the merged tree and said so with numbers: 1329 product files, 555 of them covered (the analyzer is the +1), 588 holes, 380 rows, **0 findings** — the registry still matches the measurement.

**THE MEASUREMENT WAS EMPTY BEFORE THIS.** Run 39508517419 uploaded twenty cobertura files and every one of them was `<coverage line-rate="1"><packages /></coverage>`; every suite printed "Total - Line: N/A" and the summary printed "No cobertura files produced". The cause: Release sets `DebugType=none` (`Directory.Build.props`), so the build carries no PDB, and the coverage engine on Linux instruments the assemblies STATICALLY — the instrumentation maps IL back to source lines through the portable PDB next to each DLL. With no PDB there is nothing to instrument, and an empty report reads as "100 % covered" to anything that parses `line-rate`. The lane now builds with `-p:DebugType=portable -p:DebugSymbols=true`.

### 6.9 The runner-queue wait of a pull request (#1782) ✅ measured in CI

**What the issue measured, and what the tree measures now.** Issue #1782 measured
the wait a pull request spends in the runner queue: **0.3 to 14.8 minutes**,
against 1.1-3.4 minutes of actual test time. The wait was the FAN-OUT: one
`build` job unblocked eight jobs at the same instant, and the last of them
waited for the pool to work through the seven ahead of it. That graph is gone.
The OS matrix left the pull-request lane (#1787), the coverage report moved to
the nightly lane (#1923), the shards moved to the arm64 pool (#1952), and the
`pack` job left the `build` job (#1788). The fan-out is now **eight jobs over
three pools** — five shards on `ubuntu-24.04-arm`, `pack` and `perf-gate` on
`ubuntu-24.04`, the AOT publish on `ubuntu-latest` — and the measured wait is
**1-7 s (median 2 s)**.

**How the number is obtained.** The Actions API reports each job's
`started_at` and `completed_at`, which is the queued-vs-running split the UI
hides; the wait is `started_at(job) − completed_at(build)`. The
`queue-wait` action (`.github/actions/queue-wait`, issue #1782 step 4) reads
that job list and prints the number into the job summary, so the figure is read
where the rest of the job's numbers are read. Every row below is that number,
read from the API.

**WHERE THE WAIT WAS (the old graph, 154 runs, 2026-10-20/21).** The wait was
never a property of one job: in a saturated run EVERY job of the burst waited,
and the sum of the waits was measured in thousands of seconds.

| run | fan-out | service time | worst wait | sum of the waits | who waited |
|---|---:|---:|---:|---:|---|
| [39483302243](https://github.com/refusedguy/Harbor-Harness/actions/runs/39483302243) | 6 | 865 s | **1194 s** (`test (ui)`) | 3493 s | ui 1194, coverage 785, aot 497, core 453, e2e 301, platform 263 |
| [39483558468](https://github.com/refusedguy/Harbor-Harness/actions/runs/39483558468) | 6 | 783 s | 839 s (`test (platform)`) | **4218 s** | platform 839, core 827, coverage 803, aot 623, e2e 586, ui 540 |
| [39483402840](https://github.com/refusedguy/Harbor-Harness/actions/runs/39483402840) | 7 | 822 s | 816 s (`test (ui)`) | **4183 s** | ui 816, aot 718, platform 696, e2e 589, coverage 524, core 453, perf 387 |

The mechanism is the burst's **service time** — the sum of the jobs' own times.
It was 783-882 s in those runs, and a pool that cannot absorb that much at
once serves the burst in series, so the last job of the burst waits for the
service time of the ones ahead of it. Two of the three numbers above are
therefore the same fact seen twice: the `aot-publish` job alone contributed
210-264 s of service time to every one of those bursts.

**THE CHANGE: the AOT publish starts with the build instead of after it.** The
job publishes the tree from source, restores the packages itself and downloads
no artifact of the `build` job's — its only inputs are the checkout and the
SDK. `needs: build` was a SEQUENCING choice ("start from a tree already known
to compile"), and it cost every pull request its tail: 222 s of own time
(median over 95 runs), the longest job in the post-build burst, spent AFTER
the build for no data dependency. With `needs: [changes]` the job overlaps the
build (336-436 s against 222 s) and is finished long before it.

**Measured before/after, same tree, paired runs** (the `before` is `dev`
dispatched on the same branch slot, the `after` is the same tree with the
change; the wait column is `started_at − completed_at(build)`; three pairs,
each dispatched 23 s apart so that both sides meet the same pool load).
Pairs 1-2 ran on `dev` @ `2430a88f` in a QUIET pool; pair 3 on `dev` @
`4299ce01` in a SATURATED one (34 runs queued in the repository):

| | before — [39546719740](https://github.com/refusedguy/Harbor-Harness/actions/runs/39546719740) | before — [39548019441](https://github.com/refusedguy/Harbor-Harness/actions/runs/39548019441) | after — [39546754147](https://github.com/refusedguy/Harbor-Harness/actions/runs/39546754147) | after — [39547983790](https://github.com/refusedguy/Harbor-Harness/actions/runs/39547983790) |
|---|---:|---:|---:|---:|
| `build` own time | 427 s | 371 s | 500 s | 544 s |
| fan-out jobs after the build | 8 | 8 | **7** | **7** |
| burst service time | 735 s | 716 s | **529 s** | **496 s** |
| worst wait in the burst | 56 s (`aot-publish`) | 5 s | 5 s | 12 s |
| sum of the waits in the burst | 159 s | 32 s | 26 s | 43 s |
| `aot-publish` own time | 211 s | 213 s | 208 s | 219 s |
| where the AOT publish ran | AFTER the build | AFTER the build | **with** the build | **with** the build |
| the post-build tail (last job to finish) | `aot-publish`, 56 + 211 = 267 s | `aot-publish`, 2 + 213 = 215 s | `pack`, 2 + 116 = **118 s** | `test (arch)`, 11 + 111 = **122 s** |
| the post-build phase (build end → run end) | 267 s | 215 s | **118 s** | **122 s** |

Medians over the two quiet pairs: the post-build phase **241 s → 120 s**
(−50 %), the burst's service time **725 s → 512 s** (−29 %).

The `build` job's own time differs by 73 s and 173 s between the pairs — that
is the runner, not the lane (the same spread is in §6.4's compile column), and
it is exactly why the row that carries the claim is the POST-BUILD phase: the
part of the run the change can move, measured from the build's end. The total
wall of the run moved 715 s → 644 s and 605 s → 686 s, and the second pair's
total grew because its build ran 173 s slower.

**The third pair, in a saturated pool** — the regime the issue measured. This
one is on `dev` @ `4299ce01` (with the `gate` job back) and it ran while 34
runs were queued in the repository, so the waits are the real thing: 5-488 s
per job.

| | before — [39552127159](https://github.com/refusedguy/Harbor-Harness/actions/runs/39552127159) | after — [39552162157](https://github.com/refusedguy/Harbor-Harness/actions/runs/39552162157) |
|---|---:|---:|
| `build` own time | 520 s | 547 s |
| the build's own queue wait (run → build start) | 166 s | **320 s** |
| fan-out jobs after the build | 9 | **8** |
| burst service time | 740 s | **479 s** (−35 %) |
| worst wait in the burst | 488 s (`gate`) | **407 s** (`gate`) (−17 %) |
| sum of the waits in the burst | 2449 s | **1781 s** (−27 %) |
| the post-build phase (build end → run end) | 492 s | **410 s** (−17 %) |
| the run's total wall | 1370 s | 1477 s |

**The honest reading of the third pair.** The WAIT numbers fell — the burst's
service time by 35 %, the sum of the waits by 27 %, the worst single wait by
17 % — and the post-build phase fell with them. The run's total wall did NOT
fall, and the reason is in the table: the `after` run's `build` waited 320 s
for a runner against the `before` run's 166 s. The build is in the
`ubuntu-24.04` pool and the change adds its work to the `ubuntu-latest` pool,
so that 154 s is the global queue at that moment, not the lane. The two runs
of the pair did not meet the same pool load, and the row that does not depend
on the load — the burst's service time, the waits inside the burst, the
post-build phase — fell in all three pairs.

**The tail in a saturated run is the `gate` job.** It is a 4-second job that
can only start when every lane has finished, so it asks the pool for a runner
at the busiest moment of the run: 488 s of waiting in the `before` run, 407 s
in the `after` one. No lane change can move that — the `gate` job is the
required check (#1804) and it has to be the last thing that runs. It is also
the reason the saturated pair's improvement is 17 % and not 50 %: the tail is
the queue there, not the AOT publish.

**The same change on the runs that were already in the history** (the tail =
the last job of the fan-out to finish, measured the same way):

| run | branch | `build` | fan-out | service | worst wait | the tail | wall |
|---|---|---:|---:|---:|---:|---|---:|
| [39543869492](https://github.com/refusedguy/Harbor-Harness/actions/runs/39543869492) | dev push | 429 s | 8 | 734 s | 5 s | `aot-publish` 224 s | 677 s |
| [39543921940](https://github.com/refusedguy/Harbor-Harness/actions/runs/39543921940) | PR | 336 s | 7 | 630 s | 5 s | `aot-publish` 172 s | 529 s |
| [39545372020](https://github.com/refusedguy/Harbor-Harness/actions/runs/39545372020) | PR | 377 s | 6 | 474 s | 7 s | `test (arch)` 128 s (no AOT job) | 518 s |

**THE TRADE-OFF, stated.** When the `build` is red, the AOT publish now runs
and reports its own verdict instead of being skipped. That verdict is
informative — the AOT publish compiles the product tree, not the tests, so a
pull request that breaks only a test project still gets a green AOT signal —
and the pull request was red either way. The price is 222 s of runner time on
a red pull request.

**WHAT THIS DOES NOT CLAIM.** The wait on the current graph is small (1-7 s),
so no single job-graph edit can move it much: the 14.8-minute figure belonged
to a graph that no longer exists. What the edit buys is the CRITICAL PATH
(the AOT publish's 222 s tail) and the burst's service time, which is the
quantity the wait is made of when the pool is saturated — the regime the three
runs above are measured in. The arm64 pool is the one that fills first: it
carries five shards with 347 s of service time, and it is the pool the
`test (ui)` shard waited 1194 s in.

## 7. Comparison with previous (inflated) numbers

> "Reality (this doc)" points at the section each figure lives in, so every row inherits that
> section's provenance — §3/§4 (§1 environment, 2026-07-18) for binary size, RSS and cold start;
> §6.1 (CI run 39648943782, PR #2143, 2026-10-21) for the test rows.

| Metric | Old claim | Reality (this doc) | Why differed |
|---|---:|---:|---|
| Binary size | 5 MB | 109 MB (publish folder) | Old was Debug DLL alone, not publish folder |
| RSS idle | 28 MB | ~80-120 MB (estimated) | Old was Debug `--version` exit, not real session |
| Cold start | 38 ms | 966 ms | Old was Debug incremental, not Release from cold cache |
| Test count | 242 | **9 099 discovered** (§6.1, run 39648943782) | Old was outdated; suite grew with new features |
| Test duration | 12 s | **238.6 s** of test time, `arch` shard 99 s (§6.1) | Old was after warm build, 12 projects, Debug, no-build; the suite is now 38 projects and runs in five Release shards in parallel |
| Architecture suite | 45 tests / 2.19 s (local Debug, 2026-07-18) | **1 118 discovered / 97.46 s** (CI, Release, §6.1) | Same: the old row measured a tree 25x smaller, in Debug, without the CI gate |

## 8. Per-app assessment

### 8.1 CLI (`apps/Harbor.App.Cli`)

| Aspect | Status | Notes |
|---|---|---|
| Build | ✅ green | 0 warnings, 0 errors |
| Tests | ✅ 9 078 passed of 9 099 discovered | CI, Release, five shards (§6.1); 21 skipped, 0 failed |
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
- **Test suite**: 9 099 discovered tests, 238.6 s of test time, `arch` shard 99 s (§6.1). TUnit source-gen is fast, and the four shards run in parallel.
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

## 10. The gate suite: the gates in `tools/`

The gates are a product too: every pull request pays for them, and a
gate that costs minutes costs every pull request. This section holds the
numbers for the gate set, measured the way the workflows run it.

**Method.** A clean checkout of the commit under test, with the git
history (the gates that read the history — `check-doc-cites.py` and
`check-local-merge-loss.py` — need it, and a tree without it is not the
tree CI runs). Every gate is run with the arguments its workflow passes,
from `bench/gates/tools.tsv`, five times, and the MINIMUM wall time is
kept: the machine is shared and its load is 18-26, so the minimum is the
estimate that the least foreign work distorts. The peak RSS is
`/proc/<pid>/status VmHWM`, polled while the child runs, and not
`getrusage(RUSAGE_CHILDREN).ru_maxrss`: on this host the latter carries a
value the child never reached, because it inherits the accounting of the
parent's own high-water mark. The measuring parent is a 2 KB C program
(`bench/gates/maxrss.c`) for the same reason.

**The set is the set CI runs**, and the gate `harbor-gates
check-gate-bench-set` says so: it reads the workflow files, the measured
set and the exclusion list, and fails when they disagree. §10.2 has the
numbers and the list of what is out.

### 10.1 `check-doc-cites.py`: the git processes, batched (#2090)

**A number in this section is a number about a TREE, and the tree is named
with it.** A gate that walks the tree gets slower when the tree grows, and
a gate that walks the HISTORY gets slower when the history grows:
`origin/dev` is 4051 files today and was 3876 at `a763c824`, and the
doc-cites gate reads 1291 `.cs` files under `tests/` there and 1334 here.
Two columns of one table measured on two different trees are not a
comparison, so every column below says which tree it was measured on.

**The numbers are WARM-CACHE numbers, and a CI runner is COLD.** The git
object store on the measuring machine was in the page cache; a CI runner
reads it for the first time. Measured here: the object store is copied to
a scratch directory on the same btrfs volume, the copy is evicted from the
page cache with `posix_fadvise(POSIX_FADV_DONTNEED)`, and git is pointed
at the copy with `GIT_OBJECT_DIRECTORY`. Nothing in the repository is
touched, and the copy is thrown away afterwards.

| | cold | warm |
|---|---:|---:|
| the first `git cat-file --batch` | **0.431 s** | **0.008 s** |
| the whole gate, with the batching | **55.92 s** | **26.23 s** |

**The cold cache DOUBLES the gate**: 26.23 s warm and 55.92 s cold, on the
same tree, on the same machine, minutes apart. Every number below is a
warm-cache number, and a CI runner pays the cold column on top of it. The
cold penalty is the pack read and not the gate's own work, and it lands on
the gate that reads the most history — which is the gate the batching
made cheap.

#### The measurement, on ONE tree

Two columns, ONE tree, and the tree is named in the column head: the tree
is the one at `73c0b485` (4051 files, 1334 tracked `.cs` files under
`tests/`; it was `origin/dev` when the measurement was taken), and the two
columns differ by ONE file: the gate itself. The
"before" column is the gate as `a763c824` has it, the "after" column is
the gate as the batching commit `ab4638f3` has it, dropped into the same
tree. Every other input — the documents the gate reads, the citations it
checks, the history it walks — is the same tree, so the two columns are a
controlled comparison and not a comparison of two trees.

| | before | after |
|---|---|---|
| the gate | the gate as `a763c824` has it | the batching of #2090 — **one file differs** |
| wall time, whole gate | **135.647 s** (median 151.906 s) | **21.535 s** (median 21.873 s), **6.3x** |
| `git` processes | **1277** | **37** |
| time inside `git` | **118.1 s** | **6.8 s** |
| peak RSS | 87.8 MB | 57.2 MB |
| output | the findings | **byte-identical** |

The process count and the time inside `git` are measured with a shim that
wraps `git` and records when each process starts and ends, so the numbers
are the count of the processes the gate actually started and the time the
gate actually spent waiting for them. The 1277 processes are 1231
`git blame` calls, 41 `git show` calls and 5 others; the 37 are 29
`git blame` calls (the fallback for the files whose history crosses a
merge), 4 `git ls-files`, one `git rev-parse`, one `git log`, one
`git diff` and one `git cat-file --batch`.

**What the two gate versions are.** The difference between them is 370
inserted lines and 8 deleted ones
(`git diff a763c824 ab4638f3 -- tools/check-doc-cites.py`): the per-file
`_blame_map` call is gone, and the diff-based reconstruction is in its
place. The gate file has not changed since that commit
(`git diff ab4638f3 origin/dev -- tools/check-doc-cites.py` is empty), so
the "after" column runs the SAME gate the repository runs today, and the
difference between that column and §10.2's number is the tree.

**The blame call is the expensive one**, and its cost is not the output:
it is the per-process walk of the history that finds the commit that wrote
a line. On a quiet machine that is 96 ms per call; the 347 ms the older
measurement quoted was the same call on a loaded machine.

What replaced it:

- the gate asks git about the files that HOLD a citation, not about every
  file it read. In the tree at `a763c824` **30 of the 1291** files under
  `tests/` hold a citation (94 citations), and the gate used to ask git
  about all of them;
- one `git log -p -U0` over those files, and the blame rebuilt from the
  diffs. The hunks of one commit are applied from the last to the first,
  because they all speak about the file as it was BEFORE that commit;
- a file whose reconstruction does not reproduce the file at HEAD is
  answered by `git blame` itself, in parallel. That is the fallback for the
  files whose history crosses a merge, which a diff-based reconstruction
  cannot carry across;
- the `git show` calls go through one `git cat-file --batch` process.

The reconstruction was checked against `git blame` line by line for every
file of this tree: of the files the gate uses it for, **0 disagree**.

#### What this section used to say, and why it was wrong

The revision before this one carried this table:

| | before | after |
|---|---|---|
| wall time, whole gate | **547.2 s** (median 620.4 s) | **68.1 s** (median 72.5 s), **8.0x** |
| `git` processes | **1233** | **37** |
| time inside `git` | **414 s** | **39 s** |
| peak RSS | 69.1 MB | 57.4 MB |
| output | the findings | **byte-identical** |

and named ONE tree for it: "Tree: `origin/dev` at `a763c824`, 3876 files,
1291 tracked `.cs` files under `tests/`". Three things were wrong with
that, and each of them is checkable:

1. **The tree line named a tree that cannot carry the "after" column.** The
   batching IS the commit `ab4638f3`, and `a763c824` is its ANCESTOR: the
   tree at `a763c824` has no batching at all. A column measured with the
   batching is a number about a tree that has it.
2. **The "37" is the tree that has the batching, and the "1233" is the
   tree that has not.** Measured today on `origin/dev` at `73c0b485`: the
   gate with the batching starts exactly **37** `git` processes, and the
   gate without it starts **1277** — the old table's 1233 is the same
   measurement on the tree of that day, which had 43 fewer `.cs` files
   under `tests/`.
3. **The wall times carry the machine's load.** The same comparison, on
   one tree, on a quiet machine, gives **135.6 s** and **21.5 s** (6.3x),
   and the old pair (547.2 s and 68.1 s, 8.0x) is that comparison with
   the load on: the per-process cost of a `git blame` is what the load
   multiplies, and the batching is what removes the processes.

The two tables are not in conflict: the old one is the same comparison
measured on a loaded machine, and this one is the same comparison measured
on a quiet one. What was wrong with the old one was not its numbers but
its tree line, which named a tree that was not the tree the numbers came
from.

## 10.2 The whole set, gate by gate

**The set is the set CI runs.** `bench/gates/tools.tsv` holds one row per
gate, with the arguments the workflow passes; `bench/gates/excluded.tsv`
holds the gates CI runs that the set does not measure, each with the reason
it is out (the .NET SDK, the network, or a self-test-only invocation). The
two files are checked against the workflow files by the gate
`harbor-gates check-gate-bench-set`, which fails when a gate a workflow runs
is in neither file, when a row names a gate no workflow runs, and when a row
passes arguments CI does not pass. The set below is therefore complete by
construction, not by memory: **41 gates**, and a gate added to a workflow
without a row in the table is red. The tree runner counts: a workflow that
calls `harbor-gates all` runs the gates of the runner's list, and the gate
reads that list out of the runner's Go source, so a row for a gate the
runner starts is a row for a gate CI runs.

**Method.** The tree at `536e660e` (`origin/dev` at `ff1e2955` plus this
branch's commits), with the git history. Forty-one gates, five runs per
gate, the MINIMUM wall time kept. The exit code is the gate's own: `0` is
a clean run, `1` is a run that found something, and `2` is "could not run".

**THE BOX WAS NOT QUIET, and the numbers below carry that.** The load
average was 20 to 33 during the measurement and the CPU was 99 % busy with
other agents' runs, so the numbers below are the set's cost UNDER LOAD.
Three measurements of the same harness say how much the load costs:

| what | number |
|---|---:|
| the 37-gate set on the PRE-merge tree, on this box on this day | **164.4 s** |
| the same 37-gate set on the pre-merge tree, on a QUIET machine (published before) | **39.7 s** |
| the 41-gate set on the merged tree, on this box | **222.3 s** |

The load is worth about four times, and it is worth knowing that: the
minimum of five runs is the estimate that the least foreign work distorts,
but on a box that never went quiet it is the best of five bad samples. Two
runs of the same set half an hour apart differed by nineteen per cent
(**186.0 s** and **221.1 s**, on the tree before the last merge). The table
below is the more expensive of the runs on the merged tree.

**What the merge changed, measured under one load.** The two gates that
carry the set, the same pair of trees, back to back on the same box, each
timed around the whole process:

| | pre-merge tree `1f19a8c9` | merged tree `db3fd419` | |
|---|---:|---:|---:|
| `check-doc-cites.py` | 113.5 s | 120.5 s | **+6 %** |
| `check-local-merge-loss` | 52.9 s | 52.3 s | **-1 %** |

The tree grew by sixteen commits and the set grew by four gates; the cost
moved by less than the load moved it.

| gate | wall (s) | median (s) | peak RSS | exit |
|---|---:|---:|---:|---:|
| `check-doc-cites.py` | 136.206 | 145.054 | 56.9 MB | 0 |
| `check-local-merge-loss` | 34.768 | 37.310 | 42.0 MB | 0 |
| `check-test-dup.py` | 7.968 | 10.068 | 52.9 MB | 0 |
| `check-test-projects.py` | 7.710 | 9.355 | 23.3 MB | 0 |
| `check-string-switch.py` | 7.547 | 8.169 | 18.0 MB | 0 |
| `check-result-chains.py` | 6.975 | 7.670 | 18.3 MB | 0 |
| `check-gate-policy.py` | 4.705 | 5.070 | 25.6 MB | 0 |
| `check-abstractions-contract` | 4.202 | 5.953 | 13.0 MB | 0 |
| `guard-triage.py` | 1.644 | 1.888 | 31.0 MB | 0 |
| `check-md-links.py` | 1.539 | 2.068 | 20.6 MB | 0 |
| `check-arch-anchors.py` | 1.462 | 2.560 | 17.3 MB | 0 |
| `md-lint.py` | 1.259 | 1.311 | 19.2 MB | 1 |
| `changelog-assemble` | 1.036 | 1.484 | 16.7 MB | 0 |
| `gen-seam-graph` | 1.031 | 1.242 | 79.4 MB | 0 |
| `check-arch-suite-once.py` | 0.651 | 0.939 | 17.6 MB | 0 |
| `check-event-topology` | 0.464 | 0.687 | 19.1 MB | 0 |
| `check-test-json-reflection.py` | 0.459 | 0.504 | 17.0 MB | 0 |
| `check-analyzer-scope` | 0.409 | 0.488 | 12.3 MB | 0 |
| `check-public-api-resign` | 0.289 | 0.317 | 10.9 MB | 0 |
| `normalize-mtimes` | 0.284 | 0.476 | 12.3 MB | 0 |
| `ci-shard-slices.py` | 0.260 | 0.274 | 19.4 MB | 0 |
| `check-test-packages.py` | 0.251 | 0.265 | 18.8 MB | 1 |
| `check-tool-schema-pin.py` | 0.236 | 0.249 | 19.4 MB | 0 |
| `gen-plugin-inventory.py` | 0.206 | 0.300 | 18.1 MB | 0 |
| `check-coverage-registry` | 0.160 | 0.534 | 10.8 MB | 1 |
| `coverage-report` | 0.103 | 0.172 | 11.6 MB | 0 |
| `check-doc-facts` | 0.090 | 0.098 | 9.3 MB | 0 |
| `check-xml-doc-coverage` | 0.063 | 0.082 | 11.1 MB | 0 |
| `check-no-test-sdk` | 0.051 | 0.076 | 13.5 MB | 0 |
| `aggregate-analyzer-cost` | 0.044 | 0.064 | 7.3 MB | 1 |
| `check-workflows` | 0.040 | 0.046 | 10.5 MB | 0 |
| `check-shard-balance` | 0.038 | 0.042 | 7.6 MB | 0 |
| `pack-validate` | 0.031 | 0.058 | 8.0 MB | 1 |
| `check-gate-bench-set` | 0.026 | 0.052 | 10.5 MB | 0 |
| `aot-publish-gate` | 0.023 | 0.043 | 7.1 MB | 2 |
| `test-census` | 0.020 | 0.041 | 7.2 MB | 0 |
| `aggregate-build-vcpu` | 0.016 | 0.078 | 11.4 MB | 1 |
| `demo-repro` | 0.015 | 0.033 | 7.4 MB | 0 |
| `test-measurements` | 0.014 | 0.018 | 7.2 MB | 0 |
| `test-skips` | 0.013 | 0.025 | 7.0 MB | 0 |
| `check-reference-trimmer` | 0.009 | 0.009 | 7.1 MB | 1 |
| **the whole set** | **222.317** | | | |

**The set costs 222.3 s on a loaded box**, and two gates are 77 % of it: the
doc-cites gate (136.2 s, 61 %) and the merge-loss gate (34.8 s, 16 %). The
other 39 gates together cost 51.3 s.

**PROJECTION, not a measurement: on a quiet machine the set costs about 40
to 42 s.** The pre-merge tree's quiet cost was 39.7 s for the 37 gates of
that set; the four gates this branch's merge added cost about 1.5 s under
load and about 0.4 s quiet, and the two gates that carry the set moved by
the percentages of the comparison above (+1.3 s and -0.1 s). The box never
went quiet during the measurement window, so the quiet number is not
measured here, and the honest form of it is a range.

**What the exit codes mean here.** Twelve gates read an artifact the build
produced, and the measuring machine has none of them, so they were measured
against an EMPTY directory of the right shape: the TRX directory
(`test-census`, `test-skips`, `test-measurements`, `guard-triage.py`), the
packed packages (`pack-validate`), the coverage directory
(`check-coverage-registry`, `coverage-report`), the measurement
directory (`aggregate-analyzer-cost`, `aggregate-build-vcpu`), the analyzer's
two files (`check-reference-trimmer`), the resolved package graph
(`check-test-packages.py`) and the AOT publish log (`aot-publish-gate`).
Their number is the cost of walking the tree and finding nothing, and the
parse is NOT in it: for the six gates that report a finding (exit 1) and for
the one that cannot judge what it was given (exit 2, the AOT publish log)
the number is a LOWER BOUND. The list is in
[bench/gates/README.md](../bench/gates/README.md).

**The gates that are NOT in the set** are the ones that need the .NET SDK
and the ones that need the network; they are listed with their reasons in
[bench/gates/excluded.tsv](../bench/gates/excluded.tsv). The compile-time
gates cost what §6 says: a build is 411 s, the compiler 518.8 s and the
analyzers 673.5 s.

### 10.2.1 What the set cost before: the batching and the port

The measurement above is the set as it is now. The measurement it replaced
(#2104) was of a smaller set, on older trees, and the two are not
comparable row by row. It is quoted here because it is the measurement that
decided the port, and because the numbers say what the port bought.

The three columns are three questions, and each column is a DIFFERENT tree
and a different moment:

* **python** — the gates as they were, on `origin/dev` at `a763c824`, the
  tree without the git batching and before any gate moved into the binary;
* **python + the git batching** — the same set with the batching of #2090;
* **the compiled binary** — the gates that had moved into `harbor-gates` run
  as the binary, the rest are still python.

The whole table was measured while the machine carried other people's
runs, so it carries that load: the same set on a quiet machine is 2.2
times cheaper (see the method note above). The doc-cites row is the
clearest case — 547.2 s here, 135.6 s for the same gate with the same
tree and a quiet machine (§10.1).

| gate | python | python + batching | compiled | RSS python | RSS compiled |
|---|---:|---:|---:|---:|---:|
| `check-doc-cites.py` | 547.2 | 68.1 | — | 69.1 MB | |
| `gen-seam-graph.py` | 15.9 | — | **1.0** | 120.0 MB | 78.0 MB |
| `check-test-dup.py` | 10.7 | 11.7 | — | 53.2 MB | |
| `check-test-projects.py` | 8.6 | 8.8 | — | 24.3 MB | |
| `check-string-switch.py` | 7.5 | 5.7 | — | 18.6 MB | |
| `check-string-switch-report.py` | 7.4 | 6.9 | — | 18.7 MB | |
| `check-result-chains.py` | 7.5 | 4.2 | — | 18.7 MB | |
| `guard-triage.py` | 1.8 | 0.9 | — | 31.3 MB | |
| `check-md-links.py` | 1.9 | 1.3 | — | 20.4 MB | |
| `check-abstractions-contract.py` | 1.6 | 1.6 | — | 17.5 MB | |
| `md-lint.py` | 1.4 | 0.8 | — | 19.7 MB | |
| `check-arch-anchors.py` | 1.1 | 1.0 | — | 17.8 MB | |
| `check-test-json-reflection.py` | 0.8 | 0.8 | — | 17.4 MB | |
| `check-xml-doc-coverage.py` | 0.6 | 0.7 | — | 16.7 MB | |
| `check-doc-facts.py` | 0.5 | 0.3 | — | 17.9 MB | |
| `check-analyzer-scope.py` | 0.6 | 0.3 | — | 18.4 MB | |
| `check-gate-policy.py` | 0.7 | 0.4 | — | 19.6 MB | |
| `check-result-chains-report.py` | 0.3 | 0.3 | — | 17.3 MB | |
| `check-tunit-analyzer-gate.py` | 0.3 | 0.3 | — | 16.6 MB | |
| `check-coverage-registry.py` | 0.3 | 0.3 | — | 17.7 MB | |
| `check-shard-balance.py` | 0.3 | 0.3 | — | 17.0 MB | |
| `ci-shard-slices.py` | 0.5 | 0.4 | — | 19.6 MB | |
| `check-no-test-sdk.py` | 0.3 | 0.3 | — | 17.7 MB | |
| `check-reference-trimmer.py` | 0.2 | 0.2 | — | 16.4 MB | |
| `test-census.py` | 0.2 | 0.3 | — | 17.2 MB | |
| `test-skips.py` | 0.3 | 0.2 | — | 16.5 MB | |
| `test-measurements.py` | 0.2 | 0.3 | — | 16.6 MB | |
| `pack-validate.py` | 0.2 | 0.2 | — | 16.1 MB | |
| **the whole set** | **619.2 s** | **116.7 s** | **117.6 s** | | |

Three numbers, and they are the whole answer to "what does the language
buy":

1. **The set cost 619 s**, and 547 s of it was ONE gate. The other 28
   instruments together cost 72 s.
2. **The git batching takes the set from 619 s to 117 s (5.3x)**, and it
   touches no language at all.
3. **The compiled gates take the set from 117 s to 117 s**, because the two
   gates that moved are the ones that were cheap: the seam graph is 1.6 %
   of the set.

A single gate's speed-up is not the set's speed-up. The seam graph is
**16.5x** faster as a binary (15.9 s to 1.0 s), and that is worth 2.4 %
of the set. The doc-cites gate is 88 % of the set, and its cost is git
processes, not python: 414 of its 547 seconds are inside `git`.

**What the full port would be worth, and what it would not.** If every
gate were compiled, the set would be bounded by the doc-cites gate's
python scanning: about 55 s of scanning and 13 s of git. The full port is
therefore worth roughly 619 s to 60 s, and the batching is worth 619 s to
117 s on its own.

Since then the port landed: twenty-six gates are compiled, and the set is
measured again above. The 117 s of that table is the set of 28 gates on
the tree of that day; the quiet cost of the set of 41 gates on today's
tree is 40 to 42 s (the projection above), and the two numbers differ by
the gates that were not in the old set at all, by the language mix, and by
the machine's load.

## 11. What a pull request costs

**The question this section answers.** Three numbers about the pull-request
lane were known, and none of them was the price of a pull request:

* issue [#2108](https://github.com/refusedguy/Harbor-Harness/issues/2108)
  measured the bill of ONE lane run: 19.4 runner-minutes, 17 jobs, run
  `39556560488`;
* issue [#2110](https://github.com/refusedguy/Harbor-Harness/issues/2110)
  measured what CLOSES the lane (the arch shard);
* §10 measured the gate set, on a laptop, one gate at a time.

What nobody had was the whole bill: the runner-minutes, the wall, the
critical path and the peak width of one pull request, and the split of that
bill by what the seconds buy. A decision about the lane needs all four,
because they are four different numbers and only one of them is a sum.

**THE INSTRUMENT.** `tools/gates/pr-cost` is a compiled gate, the same binary
the repository already ships (`tools/bin/harbor-gates-linux-x64 pr-cost`).
It reads what the Actions REST API returns and prints the tables below; its
input is the job list of a run, so no number in this section was transcribed
by hand. It is REPORTING: it never fails a build. Its own proof that it can
fail is its `--self-test`, which plants a stripe whose wall, busy time and
peak width are known, and a timestamp that cannot be parsed (that one must
be an ERROR, because an unreadable timestamp read as "no time" would report
a bill of zero, and zero is the number that looks like good news).

### 11.1 The sample, and what it is

Twelve pull requests, the last twelve of 2026-10-21 that built the tree and
ran the five test shards, 16:47:08 to 17:22:51 UTC. The criterion is
mechanical and is in the data: the `build` job of `ci.yml` ran, all five
shards ran, and every job of both workflows finished. A pull request that
touches only `docs/` or only `tools/` never starts the .NET build: its bill
is 4.8 runner-minutes, and it is a different shape, not a cheaper pull
request.

| | |
|---|---|
| the twelve runs | `39637885344`, `39638416505`, `39638565635`, `39638603945`, `39638705616`, `39639468264`, `39640369150`, `39640971168`, `39641227183`, `39641476017`, `39641977262`, `39643272476` |
| their head commits | `eee6a5b1`, `f7257f8d`, `6d47858e`, `90e1c345`, `9c4853d7`, `938ae03c`, `f25f2075`, `372d0285`, `b854599b`, `dbd9f9a0`, `a828bed6`, `222fa198` |
| their `docs.yml` twins | the runs started in the same second on the same branch |
| the workflows | `ci.yml` (13-14 live jobs) and `docs.yml` (21 live jobs; 22 on the one branch that adds the `workflow files` gate) |
| the pool | `ubuntu-latest`, `ubuntu-24.04`, `ubuntu-24.04-arm` |
| dev's tip that day | `13961b71` and `bb9c76b9` (dev moved twice inside the sample window) |

**WHICH ATTEMPT THE TABLES READ.** Four of the twelve had a flaky shard and
were re-run. Every table in this section reads the FIRST attempt of each run,
because that is one pass of the lane, and a second pass is a separate bill
(§11.2 names its price). The Actions API serves the LAST attempt by default
(`filter=latest`), and for those four runs the two differ: the re-run
replaces the failed job's timestamps with later ones, so the default view is
a mixture of two attempts, and its wall is 393 to 745 s longer. Reading the
first attempt needs `filter=all` and a pick of the lowest `run_attempt`
(§11.8).

**THE TREE THE TWELVE RAN ON, AND WHAT CHANGED IN THE LANE SINCE.** The
`ci.yml` of all twelve heads has the SAME job list as dev's `ci.yml` today:
the same thirteen jobs, in the same order, and the `reference-trimmer` job is
still in it. Four things changed inside the lane since:

| change | what it does to the pull-request lane |
|---|---|
| [#2123](https://github.com/refusedguy/Harbor-Harness/pull/2123) | three `DIAGNOSTIC` steps in the `build` job. They run only when the build-output cache matched, and it never matched in the twelve, so they cost 0 s there and 7 s in the run of 2026-10-22 below |
| [#2134](https://github.com/refusedguy/Harbor-Harness/pull/2134) | the `aot-publish` job's gate is the compiled binary now, not the python script. Same job, same lane |
| [#2153](https://github.com/refusedguy/Harbor-Harness/pull/2153) | the ILLink analyzer and the vs-threading family leave the ordinary build: 801.3 analyzer-seconds become 491.4, the change's own measurement. Its WALL effect was inside the 28 s noise floor of the harness that measured it, so the compile is cheaper by an amount the harness could not resolve |
| [#2155](https://github.com/refusedguy/Harbor-Harness/pull/2155), [#2158](https://github.com/refusedguy/Harbor-Harness/pull/2158) | `MSBUILDDISABLENODEREUSE=1` in the `build` job, and `HarborArchGate: 'false'` for every event. The pull-request lane already had the architecture gate off, so the second half of that change does not touch it |

Two more changed the lane's shape since, one of them still in flight. The
`go-quality` gate of PR #2162 joined the `docs.yml` gate lane: that lane
runs twenty-two jobs today, and the sample's twenty-one had no such job. The
unused-reference analyzer moves into the ordinary compile in PR #2138, and
the `reference-trimmer` job is deleted with it. That pull request is not
merged yet: the job is still in dev's `ci.yml`, and §11.6 measures the
lane it will produce.

**THE WALL IS NOT THE SUM, AND THAT IS THE POINT.** `ci.yml` and `docs.yml`
have no `needs:` between them, so they start together, and the pull request
waits for the LONGER of the two. The bill is their sum and the wall is
their maximum. A table that adds the two columns to get the wall is wrong,
and the wrongness is the expensive kind: it says every new job costs the
whole wall.

### 11.2 What one pull request costs

| pull request | runs | jobs | runner-min | wall (s) | critical path (s) | peak width | busy (s) | idle (s) | mean concurrency | the tail |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---|
| PR #39637885344 (feat/workflow-parse-gate) | 2 | 35 | **23.4** | 787 | 787 | 5 | 626 | 161 | 1.78 | `gate` |
| PR #39638416505 (test/1812-e2e-framework) | 2 | 35 | **23.8** | 796 | 796 | 5 | 582 | 214 | 1.79 | `gate` |
| PR #39638565635 (test/1553-ensure-guards) | 2 | 35 | **27.1** | 718 | 718 | 6 | 578 | 140 | 2.27 | `gate` |
| PR #39638603945 (feat/gates-a) | 2 | 34 | **23.5** | 1018 | 1018 | 4 | 707 | 311 | 1.38 | `gate` |
| PR #39638705616 (fix/ci-duplicate-yaml-key) | 2 | 34 | **23.4** | 724 | 724 | 5 | 640 | 84 | 1.94 | `gate` |
| PR #39639468264 (refactor/1569-testkit-scaffold) | 2 | 35 | **25.4** | 962 | 962 | 6 | 667 | 295 | 1.58 | `gate` |
| PR #39640369150 (refactor/1569-testkit-scaffold-b) | 2 | 35 | **24.1** | 832 | 832 | 6 | 582 | 250 | 1.74 | `gate` |
| PR #39640971168 (feat/gates-d-3) | 2 | 34 | **22.7** | 790 | 790 | 5 | 663 | 127 | 1.73 | `gate` |
| PR #39641227183 (feat/2111-msbuild-switches) | 2 | 34 | **23.1** | 983 | 983 | 6 | 641 | 342 | 1.41 | `gate` |
| PR #39641476017 (feat/gates-a) | 2 | 34 | **21.9** | 915 | 915 | 5 | 635 | 280 | 1.44 | `gate` |
| PR #39641977262 (arch/1724-semantic-guards) | 2 | 34 | **23.5** | 925 | 925 | 6 | 632 | 293 | 1.52 | `gate` |
| PR #39643272476 (feat/gates-d-3) | 2 | 34 | **21.2** | 742 | 742 | 5 | 599 | 143 | 1.72 | `gate` |

**THE HEADLINE.** A pull request in this repository costs **23.5
runner-minutes** (median; min 21.2, max 27.1) and **814 s of wall**
(median; min 718, max 1018). The critical path is the wall by definition,
from the first job's start to the last job's end, and it is always the
`gate` job that ends it.

**THE FOUR THAT NEEDED A SECOND ATTEMPT.** The re-run is a second pass of
the lane, and it is not in the tables above. Its price is WALL, not runner
time: the re-run replaces the failed shard's seconds with a new shard's
seconds, so the bill barely moves, and what it adds is the wait for a runner
and for the lane to be checked again.

| pull request | the shard that failed | first attempt: wall / runner-min | last attempt: wall / runner-min | the re-run costs |
|---|---|---|---|---|
| PR #39639468264 | `test (ui)` | 962 s / 25.4 | 1355 s / 25.4 | 393 s of wall, 0.0 runner-min |
| PR #39640369150 | `test (ui)` | 832 s / 24.1 | 1573 s / 24.1 | 741 s of wall, 0.0 runner-min |
| PR #39641227183 | `test (core)` | 983 s / 23.1 | 1728 s / 23.0 | 745 s of wall, 0.1 runner-min |
| PR #39641476017 | `test (platform)` | 915 s / 21.9 | 1306 s / 22.1 | 391 s of wall, 0.2 runner-min |

**THE ANCHOR RUN, READ BY THE SAME INSTRUMENT.** Issue #2108's number,
19.4 runner-minutes and 17 jobs, is the `ci.yml` LANE of run `39556560488`,
and the instrument reads it back exactly: ci.yml 19.4 runner-minutes over
13 live jobs (the 17 are the jobs the lane declares; four of them were
skipped). The WHOLE stripe of that run, ci.yml plus its `docs.yml` twin, is
**23.0 runner-minutes** and 909 s of wall, against the twelve's median 23.5
and 814 s, and the decomposition agrees with the twelve to within a point
per article: build 29 % against 25 %, reference trim 19 % against 19 %, test
shards 26 % against 27 %, docs gates 15 % against 16 %. The reference run
and the twelve are the same lane.

**THE TWO WORKFLOWS, SEPARATELY.** The median `ci.yml` lane is 19.3
runner-minutes over 13 jobs; the median `docs.yml` lane is 3.8
runner-minutes over 21 jobs. The `docs.yml` lane is 16 % of the bill and it
is ENTIRELY off the critical path: in all twelve runs the `ci.yml` lane is
the longer one, so the gate lane never set the wall. It starts with
`ci.yml`, runs its twenty-one gates in parallel, and finishes long before
the build does.

| pull request | workflow | jobs | runner-min | wall (s) | wait before the first job (s) |
|---|---|---:|---:|---:|---:|
| PR #39637885344 | ci.yml | 13 | 19.4 | 747 | 55 |
| PR #39637885344 | docs.yml | 22 | 4.0 | 407 | 15 |
| PR #39638416505 | ci.yml | 14 | 20.1 | 784 | 61 |
| PR #39638416505 | docs.yml | 21 | 3.6 | 693 | 49 |
| PR #39638565635 | ci.yml | 14 | 23.6 | 718 | 49 |
| PR #39638565635 | docs.yml | 21 | 3.5 | 372 | 96 |
| PR #39638603945 | ci.yml | 13 | 19.1 | 871 | 213 |
| PR #39638603945 | docs.yml | 21 | 4.4 | 835 | 66 |
| PR #39638705616 | ci.yml | 13 | 19.7 | 724 | 13 |
| PR #39638705616 | docs.yml | 21 | 3.8 | 389 | 48 |
| PR #39639468264 | ci.yml | 14 | 21.3 | 962 | 33 |
| PR #39639468264 | docs.yml | 21 | 4.0 | 648 | 88 |
| PR #39640369150 | ci.yml | 14 | 20.3 | 774 | 124 |
| PR #39640369150 | docs.yml | 21 | 3.8 | 526 | 66 |
| PR #39640971168 | ci.yml | 13 | 18.9 | 762 | 148 |
| PR #39640971168 | docs.yml | 21 | 3.8 | 294 | 120 |
| PR #39641227183 | ci.yml | 13 | 19.3 | 793 | 394 |
| PR #39641227183 | docs.yml | 21 | 3.8 | 427 | 204 |
| PR #39641476017 | ci.yml | 13 | 18.1 | 872 | 245 |
| PR #39641476017 | docs.yml | 21 | 3.8 | 339 | 202 |
| PR #39641977262 | ci.yml | 13 | 19.3 | 723 | 296 |
| PR #39641977262 | docs.yml | 21 | 4.2 | 541 | 94 |
| PR #39643272476 | ci.yml | 13 | 17.6 | 742 | 43 |
| PR #39643272476 | docs.yml | 21 | 3.6 | 265 | 71 |

### 11.3 The bill, article by article

The twelve pull requests together: **283.1 runner-minutes**.

| article | runner-min | share |
|---|---:|---:|
| build (compile) | 69.4 | **25 %** |
| build (reference trim) | 53.9 | **19 %** |
| test shards | 75.2 | **27 %** |
| docs.yml gates | 46.3 | **16 %** |
| packaging | 18.1 | 6 % |
| ci.yml gates | 13.8 | 5 % |
| aot publish | 3.6 | 1 % |
| orchestration (`changes`, `gate`) | 2.5 | 1 % |
| **total** | **283.1** | **100 %** |

**THE TWO FULL BUILDS ARE A THIRD OF THE BILL.** The `build` job compiles the
tree once (median 250 s of compile); the `unused references
(ReferenceTrimmer)` job compiles it AGAIN, from scratch, in its own job
(`.github/workflows/ci.yml:726`), and its own median wall is 274 s. Two
full compiles per pull request: 470 s of the 1410 s of a median pull
request's bill, **33 %**, and the two jobs' own walls (358 s and 274 s) are
632 s of the `ci.yml` lane's 1158 s, **55 % of the lane**, and 45 % of the
whole stripe's bill. Issue #2108 measured the same two builds as 660 s and
44 % of the bill on run `39556560488`; the shape is unchanged, and the share
grew because the gate lane got cheaper around it.

**THE BUILD JOB, STEP BY STEP** (median of the twelve; the job's own wall is
358 s, min 291, max 392):

| step | seconds |
|---|---:|
| **NUKE compile (Release, zero warnings, docs, deterministic)** | **250** (min 184, max 273) |
| setup .NET 10 (+ the NuGet cache) | 26 |
| stash the pack inputs (the `pack` job's slice) | 20 |
| stash the build outputs, core and ui shards | 10 each |
| checkout | 8 |
| the TUnit-analyzer and installer gates | 2 each |

**The ReferenceTrimmer job** (median wall 274 s): its own `dotnet build` is
**220 s** (min 161, max 265), the restore with the analyzer is 11 s, setup
.NET is 32 s.

**THE SHARDS** (median wall): `test (arch)` **124 s** (min 86, max 148),
`test (ui)` 78 s, `test (core)` 68 s, `test (platform)` 64 s, `test (e2e)`
46 s. The `pack` job's own wall is 90 s. The arch shard is the LONGEST of
the five in 12 of the 12 runs, and the last one to FINISH in 7 of the 12,
which is what issue #2110's reading of the lane's tail is confirmed by: the
lane's tail is a shard.

**THE CRITICAL PATH, STAGE BY STAGE** (median): `changes` to `build` to
shards to `gate` = **725 s** (min 640, max 883). The `gate` job itself is
2 to 4 s of work; it waits a median of **95 s** for a runner after the last
shard ends (min 32 s, max 213 s), because it is the one job that can only
start when everything else has finished.

### 11.4 What each known fix is worth

| fix | what it is | runner-minutes per pull request | wall | issue |
|---|---|---:|---:|---|
| the ReferenceTrimmer is a second full build | merging it into the main compile, or reusing the build's outputs | **not measured as a delta.** The job is still in dev and still 19 % of the sample's bill. On the lanes of 2026-10-22 the stripe costs 19.0 runner-minutes with the job and 18.5 without it, but those are two different runs with different cache states, so the difference is not the merge's price | **0 measured** | [#2108](https://github.com/refusedguy/Harbor-Harness/issues/2108) |
| the commit sha in `AssemblyInformationalVersion` | `IncludeSourceRevisionInInformationalVersion=false` (`Directory.Build.props:72`) | **0 on the runs of 2026-10-21, and that is now a fact about the cache, not about the fix.** The compile step of those twelve is 184 to 273 s (median 250 s), and the build-output cache did not fire: the "Restore the build outputs (obj/ + bin/)" step took 0 s in eight runs and 1 s in four, and the log of the newest of them says `Cache not found for input keys: msbuild-v1-…`. On the pull-request run of 2026-10-22 the cache DID fire (24 s of restore) and the compile took 45 s. The lever is the cache; the fix is what makes a restored tree valid | **0 measured**; 205 s of compile on the runs where the cache fires | [#2137](https://github.com/refusedguy/Harbor-Harness/issues/2137) |
| the arch shard is ONE project | splitting `Harbor.Architecture.Tests` into 3-4 shards | **+2.5**, three more shard jobs, each with its own setup, download and teardown. It is a COST, not a saving | **≤ 46 s**, the arch shard's 124 s minus the next-longest shard's 78 s. That difference is the ceiling; a four-way split is a projection, not a measurement | [#2110](https://github.com/refusedguy/Harbor-Harness/issues/2110) |
| the architecture gate runs twice on a dev push | switching it off inside the build, as the PR path already does | **0 on the PR lane** | **0 on the PR lane**; 132 s of the dev push's build, which #2158 has since removed | [#2114](https://github.com/refusedguy/Harbor-Harness/issues/2114) |
| the gate set | the `docs.yml` gate lane (21 gates in the sample, 22 today) and the `ci.yml` gates | **5.0** (3.8 + 1.2), 21 % of the bill | **0**, the gate lane runs in parallel with `ci.yml` | §10.2 |

**WHY THE #2108 FIX IS 19 % OF THE BILL AND 0 % OF THE WALL.** The
ReferenceTrimmer job runs in parallel with the `build` job, both need only
`changes`, and it is NOT in the `gate` job's `needs:` list
(`.github/workflows/ci.yml:3376`). The `gate` job is what the pull request
waits for, and it never waits for the trimmer. So the trimmer's 274 s are
274 s of runner time and not one second of wall. It does hold a runner of
the `ubuntu-24.04` pool for 274 s, concurrently with the build, so it is a
candidate for the queue wait below, which is a statement about the pool,
not about the job.

### 11.5 The 4 vCPU question, and the width of the lane

**The hypothesis is already falsified for the build, by measurement.**
§6.7 is the falsification: one MSBuild node is not slower than four
(ratio 1.02 on the wall, 0.95 without the architecture gate), because a
SINGLE compilation already uses 3.3 to 3.6 of the runner's four cores,
Roslyn's parallel analyzer driver inside one `CoreCompile`. More MSBuild
nodes buy nothing; more CORES do.

**What this section adds is the width of the lane itself.** The peak width
of the twelve pull requests is **4 to 6 jobs** (median 5), and the mean
concurrency over the wall is **1.38 to 2.27** (median 1.72). The lane is
not short of parallelism at its peak: the burst after the build runs the
`pack` job and five shards at once, on three runner pools.

**AND THE RUNNERS ARE NOT IDLE BECAUSE THE LANE HAS TOO FEW JOBS, THEY ARE
IDLE BECAUSE THE POOL IS QUEUED.** The idle column of §11.2 is the time the
pull request spent with NO job running: a median of **232 s of the 814 s
wall (29 %)**. That is the queue wait, and the queue is the repository's own
load: during the measured window **11 to 32 `ci.yml` and `docs.yml` runs
were in flight at once (median 20)**, against 0 to 5 (median 1) in the hour
around the runs of 2026-10-22 below. A pull request in this repository is
queued behind the merge train, and the wall is a property of the
repository's throughput, not of the lane's shape.

**SO WHAT IS THE LEVER.** Not "more parallel jobs in the lane": the lane
already fans out to six after the build, and the runner is not waiting for a
job. The two levers that the measurements point at are:

1. **the compile, on a second runner**, 250 s of the 358 s build job, and
   the build is on the critical path. §6.7 estimates ~1.4x for two
   independent clusters of the project graph, and this measurement says the
   term is 250 s of a 725 s chain;
2. **the queue**, which is not a lane property: the wall is 29 % queue, and
   §6.9's three paired runs are the same finding from the other side.

### 11.6 The same measurement on the tree of 2026-10-22

The twelve are a measurement of 2026-10-21. The lane has moved since, and
the two pull-request runs of 2026-10-22 are the same instrument read on the
tree of today. Both are pull-request runs, and the first one ran the
`ci.yml` of dev's tip as it stands now.

| pull request | tree | ci.yml jobs | ci.yml runner-min | docs.yml runner-min | the whole stripe | wall (s) | idle (s) | peak width |
|---|---|---:|---:|---:|---:|---:|---:|---:|
| PR #39719039714 (feat/2111-msbuild-switches) | dev's `ci.yml` of 2026-10-22 | 13 | 15.8 | 3.2 | **19.0** | 326 | 2 | 13 |
| PR #39718825965 (ci/2108-merge-reference-trimmer) | the same lane with the `reference-trimmer` job removed (PR #2138) | 13 | 15.0 | 3.5 | **18.5** | 472 | 5 | 16 |

**WHAT MOVED.** The stripe's bill went from the sample's 23.5
runner-minutes to 19.0, and the bill of the PR lane of 2026-10-22 splits
this way: build (compile) 3.0 runner-min (16 %), build (reference trim) 4.2
(22 %), packaging 1.6 (8 %), test shards 6.1 (32 %), ci.yml gates 0.6 (3 %),
docs.yml gates 3.2 (17 %), orchestration 0.2 (1 %). Two things moved, and
they are not the same thing:

* **the compile.** On the run of 2026-10-22 the build-output cache FIRED
  (24 s of restore) and the compile took 45 s against the sample's 250 s.
  On the run of PR #2138's lane it did not fire and the compile took 208 s.
  The cache is the lever, and it fires on some pull-request runs and not on
  others;
* **the queue.** The idle time of the two runs is 2 s and 5 s, against the
  sample's 232 s, and the repository had 0 to 5 `ci.yml` and `docs.yml` runs
  in flight at once (median 1) against the sample's median 20. The wall of
  326 s and 472 s is a wall with no queue in it, and it is not comparable
  with the sample's 814 s as a price of the lane: the lane's own work was
  324 s and 467 s, against the sample's median 634 s.

**THE PR #2138 LANE, AS MEASURED.** The `reference-trimmer` job is gone and
the analyzer runs inside the ordinary compile. The stripe bills 18.5
runner-minutes against 19.0 with the job, and the "build (reference trim)"
article is 0. The difference between the two runs is NOT the merge's price:
the compile was 208 s in one and 45 s in the other, because the cache fired
in one and not in the other. A paired measurement of that merge is still
missing.

**THE DEV-PUSH LANE, FOR THE RECORD.** The same measurement on the push to
dev of run `39717885532` (dev's tip `a019e928`, 2026-10-22 03:37 UTC) reads
16 live ci.yml jobs, 24.8 runner-minutes and 509 s of wall, with the
compile at 183 s and the trimmer's own build at 207 s. It is the push lane
and not the pull-request lane: it saves the build outputs after the compile
and it resolves the build-output cache from the previous commit, so its
compile is not the pull-request lane's compile.

### 11.7 What this section does NOT measure

* **The nightly lane and the dev-push lane.** Both are measured elsewhere
  (§6.8 for the coverage lane); the numbers here are the pull-request lane
  only. The architecture suite does not run inside the build on any lane any
  more: `HarborArchGate` is `'false'` for every event since #2158
  (`.github/workflows/ci.yml:887`), so the 132 s that #2114 measured on a
  dev push is gone from the dev push as well.
* **The pull request that does not build.** A pull request that touches only
  `docs/` or only `tools/` never starts the .NET build; its bill is 4.8
  runner-minutes (measured on run `39641146271`, the same day) and its wall
  is 542 s of gate lane. This section's own pull request is one of them:
  PR #2169 touches no `src/`, `apps/` or `tests/` file, and its own run
  `39722580870` billed 4.5 runner-minutes over 27 jobs and a 38 s wall,
  which is five ci.yml jobs and the 22-job gate lane.
* **The cost of the queue wait.** The idle time is measured; the share of
  it that belongs to a given job is not.
* **A pull request whose build fails.** The lane then runs fewer jobs (the
  shards never start): measured on run `39639464986`, whose ci.yml lane ran
  eight jobs and 16.7 runner-minutes, and whose whole stripe is 21.5
  runner-minutes and a 1053 s wall, of which 167 s is queue.
* **The runner speed.** The spread of the compile step across the twelve
  runs (184 to 273 s) is the runner's, not the tree's; §6.4 says the same
  about the lane's own numbers.
* **The price of PR #2138.** Two lanes, two runs, two cache states. The
  merge's own price is not measured here.

### 11.8 How to reproduce this section

```bash
# One file per pull request: the runs of BOTH workflows, as the API reports
# them. `filter=all` returns the jobs of EVERY attempt, and the first attempt
# is the one this section reads: the lowest `run_attempt` of the run.
stripe () {                             # $1 = run id, $2 = workflow name
  local id="$1" wf="$2" created
  created=$(gh api "repos/:owner/:repo/actions/runs/$1" --jq .created_at)
  gh api "repos/:owner/:repo/actions/runs/$1/jobs?per_page=100&filter=all" \
    | jq -s --argjson id "$1" --arg wf "$wf" --arg created "$created" \
        '([.[].jobs[]] | map(.run_attempt) | min) as $first
         | {id:$id, workflow:$wf, created_at:$created,
            jobs: [.[].jobs[] | select(.run_attempt == $first)]}'
}
{ stripe "$CI_RUN" ci; stripe "$DOCS_RUN" docs; } | jq -s '.' > run-$PR.json

# the instrument
tools/bin/harbor-gates-linux-x64 pr-cost run-*.json
tools/bin/harbor-gates-linux-x64 pr-cost --steps run-*.json   # step by step
tools/bin/harbor-gates-linux-x64 pr-cost --self-test            # the proof
```

**WHY THE FILTER IS PART OF THE RECIPE.** The Actions API serves the LAST
attempt of a run by default (`filter=latest`), and for the four runs of
§11.2 that needed a second attempt the last attempt is a mixture: the jobs
that were not re-run keep their first-attempt timestamps, and the re-run
jobs carry later ones. Dropping `--filter=all` and the pick of the lowest
`run_attempt` moves the wall of those four runs by 393 to 745 s, and the
median wall of the twelve with them.

**WHY THE RECIPE PUTS BOTH RUNS IN ONE FILE.** A pull request's bill is the
STRIPE of its two workflows, and the instrument's per-pull-request table is
a table of stripes: a file with the `ci.yml` run alone measures the lane,
and a file with both measures the pull request. The `created_at` of the run
is what the "wait before the first job" column reads, so the recipe fetches
it from the run itself.

## 12. How to reproduce

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

## 13. See also

- [Architecture layers](./ARCHITECTURE_LAYERS.md) — why the project is split this way
- [Code principles audit](./CODE_PRINCIPLES_AUDIT.md) — performance findings and fixes
- [Plugin system](./PLUGIN_SYSTEM.md) — why Roslyn is in-process today (and the plan to move it out)
- [Desktop app plan](./DESKTOP_APP_PLAN.md) — desktop app size + perf targets
