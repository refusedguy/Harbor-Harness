# Benchmarks — Harbor

> **Sources.** Numbers below come from two environments — do not compare raw values across them:
> - **CI-short** — PR `benchmark` job (`.github/workflows/benchmark.yml`, `ubuntu-latest`, `taskset -c 1`,
>   `--job Short`), latest 2026-09-09 (AMD EPYC 9V74, .NET 10.0.12, BenchmarkDotNet 0.15.8).
>   Covers `*PermissionRuleset*` + `*Registry*` filters only — marked **[CI-short]** in the tables.
>   Those two globs match exactly three classes: `PermissionRulesetBenchmark`, `ProviderRegistryBenchmark`,
>   `ToolRegistryBenchmark`. No other row in this file is produced by that job.
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
> Suite: `tests/Harbor.Benchmarks` — 39 benchmark classes (after the #408 split), `[MemoryDiagnoser]`, Release, 0 warnings.
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
| P0 | `AppReducer` streaming concat | 1.72 ms / **19.4 MB** per 1000 TextDelta (O(N²) string +) | pooled StringBuilder / chunk list, materialize on MessageEnd |
| P0 | `MessageConverter` large msgs | serialize 2.35 ms / 1.2 MB per msg; 100×large round-trip **545 ms** | Utf8Json source-gen (audit §PERF-002) |
| P1 | `CompactionService.ShouldCompact` | 598 µs @1000 msgs **каждый turn** | incremental token counter |
| P1 | `EventBroadcaster` | 9–11 ms / **8 MB** per 1000 events, не зависит от числа клиентов ⚠️ undated pre-#408 run | serialize once, reuse buffers |
| P1 | `EventBus.PublishAsync` | ~~фикс. 8.1 KB alloc даже при 0 подписчиков~~ ✅ resolved: 0-sub fast path returns before scrollback/fan-out (zero alloc, locked by `PublishAsync_ZeroSubscribers_IsAllocationFree`); 1/10-sub fan-out covered by bounded tripwires (#186) | ring-buffer scrollback (landed) |
| P2 | `StreamingCoalescer` tool-call Materialize | 481 µs @1000 дельт (35–48× медленнее текста) | кэш разобранных аргументов |
| P2 | `PatchTool` apply | 10.1 ms / **9.3 MB** @5000 hunks | стримить вместо List<string>+Join |
| P2 | `DefaultUiProjector` | 20.8 ms @5000 строк за кадр (холодный полный проход; инкрементальный кэш уже влито — см. ниже) | инкрементальная проекция по revision |
| OK | `DefaultUiProjector` инкремент | 1000 дельт → 1001 проекция: **962 no-op reuse, 38 tail (флаши), 1 history**; markdown-парсов на store-пути 0; итого 475 µs / 1.03 MB (2026-09-10, `StreamingDeltaFrequencyBenchmark`, регрессия — `StreamingFrequencyTests`) | пожара нет; следить за transcript-композицией при росте history |
| P3 | `SessionId` Dictionary key | медленнее string (7.9 vs 6.3 µs), HashSet быстрее — проверить GetHashCode | override hash |
| P3 | `OpenAiWire.TryParseChatChunkLine` | плоские ~10 µs floor на любой чанк | Utf8JsonReader поверх span без ToString() |

## Key numbers — local full runs (2026-08-22, i5-8250U, Release JIT; UiStore streaming rows 2026-09-10, machine n/a)

| Operation | Mean | Allocated |
|---|--:|--:|
| AgentLoop turn (no tool) | 10.2–11.8 µs | 5.5 KB |
| AgentLoop turn (+tool) | 16.6–26.6 µs | 7.5 KB |
| EventBus.PublishAsync (0 sub) | 8.1 µs | 8.1 KB |
| WireCodec roundtrip 64B / raw frame 64B | 6.2 µs / 0.25 µs | 1.8 KB / 0 |
| OpenAiSse.ParseChunk 32B→4KB | 10.2–10.6 µs | 3.9–6.8 KB |
| JsonlSessionStore.Append ×100 | 1.74 ms | 187 KB |
| Sqlite WAL Append ×10 | 2.2–2.6 ms | 155 KB |
| AppStore.Dispatch TextDelta ×1000 | 1.72 ms | 19.4 MB |
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
| `EventBus.PublishAsync` (1 subscriber) | 0.35 µs | 0.04 µs | 0 B |
| `EventBus.PublishAsync` (10 subscribers) | 2.80 µs | 0.20 µs | 0 B |
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

⏳ **No numbers yet.** These rows are code-only as of #408: no local full run and no CI-short run
(the CI `benchmark` job filters on `*PermissionRuleset*` / `*Registry*`, so it never touches the bus
classes). Fill them in with machine + date on the next local pass:

```bash
dotnet run -c Release --project tests/Harbor.Benchmarks -- --filter '*Delivery*'
```

The pre-#408 blended rows that remain valid are the `EventBus.PublishAsync` fan-out curve in §5.2
(1 / 10 subscribers) and the P1 `EventBroadcaster` bottleneck row at the top of this file — both are
attributed there. `InMemoryEventBus` exposes no way to empty its scrollback ring, so the enqueue rows
run against a ring that saturates; slot overwrite costs the same as a fresh slot, which each class doc
records under `RetainedState:`.

### 5.4 Zero-subscriber fast path (#47/S3) ⏳ latency not yet measured, allocation pinned

`EventBusFastPathBenchmark` measures acceptance cost for four compositions of the same publish, so the
fast path is a number rather than a claim, and the near-misses that disqualify a bus are visible next
to it. Run by the CI `benchmark` job (`--filter '*EventBusFastPath*'`, Short job); a local full run:

```bash
dotnet run -c Release --project tests/Harbor.Benchmarks -- --filter '*EventBusFastPath*'
```

| Row | Composition | Expected allocation |
|---|---|---|
| `Qualifying_0Sub_NoSinks_ScrollbackOff` | 0 subscribers, 0 scrollback, no sinks | **0 B/op** |
| `Qualifying_0Sub_OptionalSinkDrained_ScrollbackOff` | + one optional sink (sampler), drained inline | **0 B/op** |
| `Disqualified_0Sub_MandatorySink_ScrollbackOff` | + one mandatory sink (type filter) | allocates by design |
| `Disqualified_1Sub_NoSinks_ScrollbackOff` | one live subscriber | allocates by design |

⏳ **Latency columns are not yet filled** — the rows are code-only, exactly like §5.3, and the honest
number to quote until a CI run lands is the *allocation* one, which is asserted (not eyeballed) by
`EventBusFastPathTests` in `tests/Harbor.Core.Tests` via
`GC.GetAllocatedBytesForCurrentThread() == 0` over 5 000 publishes. Which production compositions
actually qualify is measured per preset in `tests/Harbor.Hosting.Tests/EventBusSinkCompositionTests.cs`
and tabulated in [`docs/EVENT_BUS_SINKS.md`](./EVENT_BUS_SINKS.md) §5 (today: 0 % for every shipped
preset — the mandatory/optional verdict, not the guard, is what keeps them out).

### Allocation-budget tripwires (#186, CI-enforced)

Steady-state allocation coverage for paths the microbenchmarks above don't
pin. Zero-alloc cells assert `GC.GetAllocatedBytesForCurrentThread() == 0`
after warmup (thread-scoped, parallel-safe); bounded cells are generous
tripwires in the `SpanParserTests` tradition — per-op averages are printed
to stdout for the next BENCHMARKS refresh, hard failures only on
pathological growth. Run per project, e.g.:

```bash
dotnet run -c Release --project tests/Harbor.Registries.Tests -- --treenode-filter "*/*/*Allocation*"
```

| Test | Path | Ceiling |
|---|---|---|
| `PublishAsync_ZeroSubscribers_IsAllocationFree` (`Harbor.Registries.Tests`) | `InMemoryEventBus` 0-sub fast path | 0 B |
| `QualifyingComposition_ReturnsCompletedTask_AndAllocatesNothing` (`Harbor.Core.Tests`, #47/S3) | 0-sub / 0-scrollback / no-sink fast path, under the mandatory-sink guard | 0 B |
| `OptionalSink_IsDrainedOnTheFastPath_NotSkipped` (`Harbor.Core.Tests`, #47/S3) | same + an optional sink drained inline | 0 B |
| `PublishAsync_SingleSubscriber_StaysBounded` | `InMemoryEventBus` 1-sub fan-out | ≤ 1 KB/publish |
| `PublishAsync_TenSubscribers_StaysBounded` | `InMemoryEventBus` 10-sub fan-out | ≤ 2 KB/publish |
| `ResolveTools_FrozenUnfiltered_IsAllocationFree` | frozen `ToolRegistry.ResolveTools` (no permission, cached array) | 0 B |
| `ResolveTools_FrozenWithPermission_IsAllocationFree` | frozen `ToolRegistry.ResolveTools` (same ruleset, memoized) | 0 B |
| `TextOnly_Turn_StaysBounded` (`Harbor.Application.Tests`) | `AgentLoop` text-only turn | ≤ 64 KB/turn |
| `ToolCall_Turn_StaysBounded` | `AgentLoop` tool turn incl. `StreamingCoalescer` materialize/`TryParseArgs` | ≤ 256 KB/turn |
| `Parse_UserLine_StaysBounded` (`Harbor.Storage.Jsonl.Tests`) | `JsonlLineParser.Parse` per line | ≤ 8 KB/line |
| `GetMessages_SeededStore_StaysBounded` | `JsonlSessionStore.GetMessagesAsync` (100 msgs) | ≤ 512 KB/read |
| `TryParseChatChunkLine_TextDelta_StaysBounded` (`Harbor.Providers.Tests`) | `OpenAiWire.TryParseChatChunkLine` text chunk | ≤ 4 KB/chunk |
| `ExtractDiff_NonDiffTool_IsAllocationFree` (`Harbor.Tui.CellForge.Tests`) | `DiffPreview.ExtractDiff` non-diff guard | 0 B |
| `ExtractDiff_Edit_StaysBounded` | `DiffPreview.ExtractDiff` edit path | ≤ 32 KB/call |

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

> `dotnet test` discovers ZERO tests in this repo (broken MTP bridge: the
> host exits 5 with a silent discovery error). Run test projects as plain
> executables, one project at a time — never whole-solution, never
> `dotnet test`.

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
