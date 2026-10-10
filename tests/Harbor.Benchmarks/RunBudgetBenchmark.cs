using BenchmarkDotNet.Attributes;
using Harbor.Abstractions.Models;
using Harbor.Application.Agents;

namespace Harbor.Benchmarks;

/// <summary>
///     Benchmarks <see cref="RunBudgetTracker.CheckCap" /> (#404), the per-turn/per-delta
///     budget gate. Measures:
///     - the no-trip check with caps set (the steady-state cost of a capped run),
///     - the streaming delta path (<c>RecordOutputBytes</c> + <c>CheckCap</c>).
/// </summary>
/// <para><b>Measurement contract (#408)</b> — what this number includes:</para>
/// <list type="bullet">
///          <item><c>Operation:</c> one <c>CheckCap</c> call (three threshold compares behind
///          a lock), or one <c>RecordOutputBytes</c> + <c>CheckCap</c> pair — the work the
///          loop does per turn and per streamed delta when the agent sets budget caps.</item>
///          <item><c>Payload:</c> caps of 1M tokens / $1000 / 1M bytes (never tripped) and a
///          24-byte delta — small constants, no JSON document.</item>
///          <item><c>StateReset:</c> none — the tracker is built once in <c>Setup</c>; the delta
///          row accumulates output bytes across iterations but never trips within a Short job
///          (thousands of 24-byte adds against a 1M cap), so every iteration measures the
///          same no-trip path.</item>
///          <item><c>Drain:</c> none — both calls are synchronous.</item>
///          <item><c>RetainedState:</c> the caps record and the running counters inside the
///          tracker; no per-call memoisation.</item>
///          <item><c>AwaitSemantics:</c> n/a — no async in any row.</item>
///          <item><c>AllocAttribution:</c> the check is compares under a lock — a non-zero
///          value on a row is a regression, not overhead. The uncapped path (null tracker,
///          one null-branch per turn) is not benchmarked: there is nothing to measure.</item>
/// </list>
[MemoryDiagnoser]
[SimpleJob(warmupCount: 3, iterationCount: 5)]
public class RunBudgetBenchmark
{
    private RunBudgetTracker _tracker = null!;

    [GlobalSetup]
    public void Setup()
    {
        _tracker = new RunBudgetTracker(new RunBudgetCaps(MaxTokens: 1_000_000, MaxCostUsd: 1000m, MaxOutputBytes: 1_000_000));
    }

    [Benchmark(Description = "CheckCap (caps set, no trip)", Baseline = true)]
    public RunLimitKind? CheckCap_NoTrip()
        => _tracker.CheckCap();

    [Benchmark(Description = "RecordOutputBytes + CheckCap (streaming delta path)")]
    public RunLimitKind? RecordOutputBytes_AndCheck()
    {
        _tracker.RecordOutputBytes(24);
        return _tracker.CheckCap();
    }
}
