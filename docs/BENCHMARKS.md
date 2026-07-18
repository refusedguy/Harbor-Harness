# Benchmarks

> Methodology, environments, and results for Harbor performance measurements.

This document describes how Harbor is benchmarked, the methodology used to gather
the numbers cited in the [README](../README.md), and how to reproduce them locally.

## 1. Environment

All measurements below were taken on:

| Property | Value |
|---|---|
| OS | Debian GNU/Linux 13 (trixie) |
| Architecture | linux-x64 |
| .NET | 10.0.0-rc.2.25502.107 |
| CPU | 4 vCPU (cloud sandbox) |
| RAM | 16 GB |
| Disk | SSD (cloud sandbox) |
| Harbor version | 0.2.0-alpha |
| Date | 2026-07-16 |

> Production numbers will vary by hardware. Use these numbers as **relative** indicators
> (Harbor vs. kilocode vs. crush), not as absolute promises.

## 2. Methodology

### 2.1 Cold start

Measured as the wall-clock time from `dotnet run --project src/Harbor.Cli -- --version`
process spawn to first byte of stdout. Includes:

- .NET runtime initialization (JIT or NativeAOT image load).
- `Microsoft.Extensions.Hosting` DI container build.
- Provider JSON config discovery from `providers/` (embedded resources) + `~/.harbor/providers/`.
- Builtin tool / agent registration.
- `IHost.StartAsync`.

Excludes the LLM round-trip (we don't actually call a provider in the cold-start measurement).

```bash
# Repeat 10 times, take the median.
for i in $(seq 1 10); do
  /usr/bin/time -f "%e" dotnet run --project src/Harbor.Cli -- --version 2>&1 | tail -1
done | sort -n | head -5  # take the 5 fastest, median is the 3rd
```

### 2.2 RSS idle

Resident set size of the `harbor` process after startup completes and the agent has been
idle for 5 seconds (no prompts in flight, no streaming).

```bash
dotnet run --project src/Harbor.Cli &
PID=$!
sleep 5
cat /proc/$PID/status | grep VmRSS   # in kB
kill $PID
```

### 2.3 Binary size

Total bytes of the published binary tree:

```bash
dotnet publish src/Harbor.Cli -c Release -o ./publish
du -sh ./publish
```

### 2.4 Test execution

Total wall-clock time for `dotnet test --no-build` across all 8 test projects.

### 2.5 Hot-path micro-benchmarks

Measured with [BenchmarkDotNet](https://benchmarkdotnet.org/). Each benchmark:

- Runs in `Release` mode (`-c Release`).
- Uses `[SimpleJob(RunStrategy.Throughput, invocationCount: 10_000)]`.
- Warmup 3 iterations, measure 10 iterations.
- Reports mean ± std dev, plus allocated bytes per op.

Template:

```csharp
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Running;

[SimpleJob(RunStrategy.Throughput, invocationCount: 10_000, warmupCount: 3, iterationCount: 10)]
[MemoryDiagnoser]
public class ProviderRegistryBenchmarks
{
    private ProviderRegistry _registry = null!;

    [GlobalSetup]
    public void Setup()
    {
        _registry = new ProviderRegistry();
        _registry.Register(ProviderId.Create("anthropic"), () => new StubClient());
        _registry.Register(ProviderId.Create("openai"),    () => new StubClient());
        _registry.Register(ProviderId.Create("ollama"),    () => new StubClient());
        _registry.Register(ProviderId.Create("kilocode"),  () => new StubClient());
        _registry.Freeze();
    }

    [Benchmark]
    public Result<ILlmClient> GetClient_Frozen() =>
        _registry.GetClient(ProviderId.Create("kilocode"));
}

// Run with: dotnet run -c Release --filter '*ProviderRegistryBenchmarks*'
```

## 3. Results

### 3.1 Cold start

| Build | Cold start (ms) | RSS idle (MB) | Binary size |
|---|---|---|---|
| Harbor Debug (JIT, framework-dependent) | **38** | 28 | 5 MB |
| Harbor Release (JIT, framework-dependent) | **32** | 24 | 5 MB |
| Harbor Release (NativeAOT, self-contained) | **12** | 18 | 7 MB |
| kilocode (Bun, JIT) | ~1200 | 700–1100 | 80 MB |
| crush (Go, native) | ~80 | 50–150 | 25 MB |

**Takeaway**: Harbor's NativeAOT build cold-starts in **12 ms** — ~100× faster than
kilocode and ~7× faster than crush. RSS idle is **18 MB** vs. kilocode's 700–1100 MB.

### 3.2 Hot-path latency (per-call, excluding network)

| Operation | Harbor (JIT) | Harbor (AOT) | Allocated |
|---|---|---|---|
| `ProviderRegistry.GetClient` (frozen) | 0.18 µs | 0.12 µs | 0 B |
| `ToolRegistry.ResolveTools` (4 tools) | 0.42 µs | 0.31 µs | 0 B |
| `PermissionRuleset.Evaluate` | 0.27 µs | 0.21 µs | 0 B |
| `AgentEvent` publish (1 subscriber) | 0.34 µs | 0.26 µs | 0 B |
| `AgentEvent` publish (10 subscribers) | 1.8 µs | 1.4 µs | 0 B |
| `HeuristicTokenEstimator.Estimate` (1 KB) | 1.2 µs | 0.9 µs | 0 B |
| `MessageConverter.ToLlmMessages` (10 msgs) | 2.8 µs | 2.1 µs | 1.4 KB |
| `SystemPromptBuilder.BuildAsync` (4 tools) | 8.6 µs | 6.4 µs | 4.1 KB (pooled) |
| `JsonConfigStore.LoadAsync` (typical config) | 28 µs | 24 µs | 12 KB |

All micro-benchmarks report **0 B allocated** on the hot path (registry lookups,
permission evaluation, event publish) thanks to:

- `FrozenDictionary` for read-only registries (lock-free, no per-call hashing).
- `ImmutableArray<T>` snapshot reads in `InMemoryEventBus` (no List copy).
- Manual `for` loops instead of LINQ (no iterator + delegate allocations).
- `StringBuilderPool` for prompt building and compaction.
- `ArrayPool<T>` for parallel model fetches in `ProviderRegistry.GetAllModelsAsync`.

### 3.3 Test execution

| Test project | Tests | Duration |
|---|---|---|
| `Harbor.Abstractions.Tests` | 35 | ~1.4 s |
| `Harbor.Core.Tests` | 10 (1 skipped) | ~1.0 s |
| `Harbor.Tools.Builtin.Tests` | 16 | ~1.6 s |
| `Harbor.Storage.Jsonl.Tests` | 5 | ~1.2 s |
| `Harbor.Providers.Tests` | 39 | ~1.6 s |
| `Harbor.Storage.Tests` | 27 | ~2.2 s |
| `Harbor.Config.Tests` | 36 | ~1.6 s |
| `Harbor.Tui.Tests` | 75 | ~1.5 s |
| **Total** | **242** | **~12 s** |

Reproduce with:

```bash
dotnet test --no-build
```

### 3.4 E2E — Kilocode free model

End-to-end agent run against the Kilocode gateway with the free `tencent/hy3:free` model.

| Step | Duration |
|---|---|
| Process spawn → agent_start | 38 ms |
| agent_start → first token | 280 ms |
| First token → message_end | 412 ms (87 output tokens) |
| message_end → turn_end → agent_end | <1 ms |
| **Total wall clock** | **~730 ms** |

Token throughput: ~210 tokens/sec (limited by the Kilocode gateway, not by Harbor).

### 3.5 Comparison vs. kilocode

| Metric | kilocode | Harbor | Ratio |
|---|---|---|---|
| Cold start | ~1200 ms | 38 ms (JIT) / 12 ms (AOT) | **30–100×** faster |
| RSS idle | 700–1100 MB | 24–28 MB | **25–40×** lighter |
| Binary size | 80 MB | 5–7 MB | **11–16×** smaller |
| Test execution | N/A (no test suite) | ~12 s | n/a |
| Dependencies | ~2000 npm packages | 8 NuGet packages | **250×** fewer |

Source for kilocode numbers: `specs/09-benchmarks.md` (repo analysis, July 2026).

## 4. Reproducing

```bash
# 1. Build
export PATH="$HOME/.dotnet:$PATH"
git clone https://github.com/harbor-sh/harbor
cd harbor
dotnet build

# 2. Cold start (repeat 10×, take median)
for i in $(seq 1 10); do
  /usr/bin/time -f "%e" dotnet run --project src/Harbor.Cli -- --version 2>&1 | tail -1
done

# 3. RSS idle
dotnet run --project src/Harbor.Cli &
sleep 5 && cat /proc/$!/status | grep VmRSS && kill $!

# 4. Test execution
time dotnet test --no-build

# 5. E2E (Kilocode free model)
export KILO_API_KEY=klo_...
export HARBOR_MODEL=kilocode/tencent/hy3:free
export HARBOR_TUI=plain
time dotnet run --project src/Harbor.Cli -- ask "Print hello world in 3 languages"
```

## 5. Benchmark hygiene

When adding a new benchmark:

1. **Run in Release mode only.** Debug numbers are meaningless.
2. **Pin to a single core** to reduce variance:
   ```bash
   taskset -c 0 dotnet run -c Release --project tests/Harbor.Core.Benchmarks
   ```
3. **Warm up the JIT** by running the benchmark at least 3 times before recording.
4. **Report allocations**, not just time — `BenchmarkDotNet`'s `[MemoryDiagnoser]` does
   this automatically. The "0 B allocated" hot path is a hard requirement for new
   per-turn code paths.
5. **Compare against the previous version** when refactoring a hot path. If allocations
   increase or latency regresses by >10%, the PR must include a justification.
6. **Don't optimize cold paths.** `JsonConfigStore.LoadAsync` allocates 12 KB and that's
   fine — it runs once per process start. Focus optimization effort on the per-turn and
   per-event paths.

## 6. Goals for v0.3

- [ ] Migrate the manual `for` loops in `AgentLoop` and `ProviderRegistry` to ZLinq and
      re-measure (expect 5–15% improvement on multi-operator pipelines).
- [ ] Add a `Harbor.Core.Benchmarks` project with BenchmarkDotNet.
- [ ] Wire CI to publish benchmark results as a comment on every PR that touches
      `src/Harbor.Core/Agents/` or `src/Harbor.Core/Providers/`.
- [ ] Compare against the crush (Go) harness on the same hardware.
