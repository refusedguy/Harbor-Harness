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
> - **⏳ not yet measured** — the benchmark exists in `tests/Harbor.Benchmarks` but has no run recorded
>   here yet. Never invent a value for these; run the row locally and fill it in with machine + date.
>   Introduced by the #408 bus split, whose rows are code-only until the next local pass.
>
> **How to read any row in this file:** it lives under a section header that names the machine, the
> date and the job, or it carries an explicit `⏳ not yet measured` / `⚠️ retracted` marker. There is
> no unattributed row — that is the litmus for #408.
>
> Suite: `tests/Harbor.Benchmarks` — 40 benchmark classes (after the #408 split and the #391
> EventBus rewrite), `[MemoryDiagnoser]`, Release, 0 warnings.
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
| OK | `DefaultUiProjector` инкремент | 1000 дельт → 1001 проекция: **962 no-op reuse, 38 tail (флаши), 1 history**; markdown-парсов на store-пути 0; итого 475 µs / 1.03 MB (2026-09-10, `StreamingDeltaFrequencyBenchmark`, регрессия — `StreamingFrequencyTests`) | пожара нет; следить за transcript-композицией при росте history |
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
| ToolRegistry.ResolveTools frozen @4 (with permission) | 2.3 µs | 88 B |
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
| App projects (`apps/`) | 2 (`Harbor.App.Cli`, `Harbor.App.Avalonia`; WPF/MAUI/Blazor переехали в `contrib/apps/`) |
| Source projects (`src/`) | 51 |
| Test projects (`tests/`, csproj dirs) | 27 |
| Sample plugins (`samples/`) | 4 |
| **Total in `Harbor.slnx`** | **~84** (без contrib; по данным текущего `Harbor.slnx`: src+tests+apps+samples+build-проект) |

> Составы ниже соответствуют состоянию на дату замера (2026-07-18) — тогда в слюнксе
> было ~60 проектов; сегодня счётчик выше приведён к актуальной структуре.

### 2.2 Build time

| Step | Duration |
|---|---|
| `dotnet restore` (cold, no cache) | ~6 s |
| `dotnet build` Debug (incremental, warm) | ~25 s |
| `dotnet build -c Release` (incremental, warm) | ~75 s |
| `dotnet build -c Release --no-incremental` (full) | ~120 s |

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

**Status: not yet supported.** Harbor CLI cannot be NativeAOT-published today because of:
- `Spectre.Console` reflection usage (optional Spectre renderers referenced via the `HARBOR_WITH_SPECTRE_TUI` build flag; contrib projects)
- `Microsoft.CodeAnalysis.CSharp` (Roslyn) — not AOT-compatible
- In-process CS-source plugin compilation: `src/Harbor.Plugins.Compilation/RoslynPluginCompiler.cs`

**Roadmap:** out-of-process plugin host skeleton already exists (`Harbor.Plugins.Host` exe, MCP stdio); finishing the split (planned v0.9 two-process milestone) plus dropping in-process Roslyn and Spectre reflection unlocks AOT for the main process. Expected AOT binary size: ~14-20 MB (typical for .NET 10 AOT console apps with similar deps).

If you still want to try AOT today:
```bash
dotnet workload install native-aot
cd apps/Harbor.App.Cli
dotnet publish -c Release -r linux-x64 -p:PublishAot=true
# Expected: IL2026 warnings from Spectre + Roslyn, runtime crash on first plugin compile
```

### 3.4 Framework-dependent vs Self-contained

| Mode | CLI publish size | When to use |
|---|---:|---|
| Framework-dependent (default) | 109 MB | Dev / power users with .NET 10 installed |
| Self-contained `--self-contained` | ~85 MB + ~75 MB runtime = **160 MB** | End users without .NET |
| Self-contained + `PublishSingleFile` | ~85 MB single binary + ~75 MB runtime files | Distribution |
| Self-contained + `PublishTrimmed` | ~50 MB | Experimental — breaks Spectre.Console reflection |
| NativeAOT (when supported) | ~14-20 MB | Production target |

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
| `ExtractDiff_NonDiffTool_IsAllocationFree` (`Harbor.Tui.CellForge.Tests`) | `DiffPreview.ExtractDiff` non-diff guard | 0 B |
| `ExtractDiff_Edit_StaysBounded` | `DiffPreview.ExtractDiff` edit path | ≤ 32 KB/call |
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

## 6. Test suite

### 6.1 Per-project results (Debug, no-build) — local run, linux-x64 container, .NET 10.0.302, pre-#186 counts

| Test project | Passed | Failed | Skipped | Duration |
|---|---:|---:|---:|---:|
| `Harbor.Abstractions.Tests` | 34 | 0 | 0 | 404 ms |
| `Harbor.Core.Tests` | 51 | 0 | 1 | 694 ms |
| `Harbor.Tui.Tests` | 220 | 0 | 0 | 1.44 s |
| `Harbor.Tools.Builtin.Tests` | 69 | 0 | 1 | 1.21 s |
| `Harbor.Plugins.Runtime.Tests` | 23 | 0 | 0 | 3.36 s |
| `Harbor.Scripting.Tests` | 51 | 0 | 0 | 897 ms |
| `Harbor.Architecture.Tests` | 45 | 0 | 0 | 2.19 s |
| `Harbor.Config.Tests` | (all pass) | 0 | 0 | ~500 ms |
| `Harbor.Providers.Tests` | (all pass) | 0 | 0 | ~500 ms |
| `Harbor.Storage.Jsonl.Tests` | (all pass) | 0 | 0 | ~500 ms |
| `Harbor.Storage.Tests` | (all pass) | 0 | 0 | ~500 ms |
| `Harbor.Tui.E2E.Tests` | (requires terminal) | — | — | — |
| **Total (measured)** | **~493** | **0** | **2** | **~12 s** |

> +12 allocation-budget tripwires added in #186 (this change): 5 in
> `Harbor.Registries.Tests`, 2 in `Harbor.Application.Tests`, 2 in
> `Harbor.Storage.Jsonl.Tests`, 1 in `Harbor.Providers.Tests`, 2 in
> `Harbor.Tui.CellForge.Tests`. Counts above predate them.

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

## 7. Comparison with previous (inflated) numbers

> "Reality (this doc)" points at the section each figure lives in, so every row inherits that
> section's provenance — §3/§4 (§1 environment, 2026-07-18) for binary size, RSS and cold start;
> §6.1 (local run, pre-#186 counts) for the test rows.

| Metric | Old claim | Reality (this doc) | Why differed |
|---|---:|---:|---|
| Binary size | 5 MB | 109 MB (publish folder) | Old was Debug DLL alone, not publish folder |
| RSS idle | 28 MB | ~80-120 MB (estimated) | Old was Debug `--version` exit, not real session |
| Cold start | 38 ms | 966 ms | Old was Debug incremental, not Release from cold cache |
| Test count | 242 | ~493 | Old was outdated; suite grew with new features |
| Test duration | 12 s | ~25-30 s | Old was after warm build; cold is slower |

## 8. Per-app assessment

### 8.1 CLI (`apps/Harbor.App.Cli`)

| Aspect | Status | Notes |
|---|---|---|
| Build | ✅ green | 0 warnings, 0 errors |
| Tests | ✅ ~493 pass | All non-E2E tests pass |
| Binary | 109 MB publish | Roslyn + Spectre dominate |
| AOT | ❌ not supported | Spectre reflection + Roslyn dynamic |
| Cold start | 966 ms | Target: <100 ms with AOT |

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
- **Test suite**: ~493 tests in ~12 s (no-build) — TUnit source-gen is fast.
- **Lock-free `UiStore.Dispatch`**: 0.15 µs via CAS loop, no contention.

### What's actually slow / bloated

- **Cold start 966 ms**: dominated by `dotnet` host + assembly load. NativeAOT would fix this (target <100 ms).
- **Publish folder 109 MB**: Roslyn (30+ MB for plugin compilation) is the elephant. Moving plugin host out-of-process would cut ~30 MB.
- **`JsonlSessionStore.GetMessages`**: 850 µs for 100 messages, 28 KB allocated — measured before the `Utf8JsonReader` span rewrite (`JsonlLineParser`); current behavior locked by #186 tripwires, re-measure before quoting.
- **No AOT**: Spectre.Console reflection + Roslyn dynamic compilation block NativeAOT today.

### What was misleading in old benchmarks

- "5 MB binary" was the Debug DLL, not the publish folder.
- "28 MB RSS idle" was a Debug `--version` run that exits immediately.
- "38 ms cold start" was Debug incremental ( assemblies already loaded in dotnet cache).
- These numbers were aspirational, not measured.

### Roadmap to actually hit "fast" targets

1. **Move Roslyn plugin host out-of-process** (v0.7) — cuts ~30 MB from CLI publish, enables AOT for the main process.
2. **Replace Spectre.Console with Spectre.TUI source-gen** — removes reflection, enables AOT.
3. **`Utf8JsonReader` rewrite for `JsonlSessionStore`** — 5x faster, 80% less allocation.
4. **NativeAOT publish for CLI** — target: 14-20 MB binary, <100 ms cold start, ~30 MB RSS idle.
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
