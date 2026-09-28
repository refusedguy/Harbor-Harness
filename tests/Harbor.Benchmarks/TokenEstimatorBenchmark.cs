using BenchmarkDotNet.Attributes;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Sessions;
using Harbor.Application.Sessions;
namespace Harbor.Benchmarks;
/// <summary>
///     Benchmarks <see cref="HeuristicTokenEstimator" /> on text payloads of
///     varying sizes. The estimator counts CJK chars (×0.5 tokens) and
///     non-CJK chars (×0.25 tokens), so English-only inputs are O(n) char
///     scans with no allocations.
/// </summary>
/// <para><b>Measurement contract (#408)</b> — what this number includes:</para>
/// <list type="bullet">
///          <item><c>Operation:</c> one <c>Estimate</c> call over a text payload, or one
///          <c>EstimateMessage</c> over a two-part assistant message.</item>
///          <item><c>Payload:</c> 100-char, 4 K-char, 64 K-char ASCII strings and a 2 K-char
///          CJK/ASCII mix, all pre-built in <c>Setup</c>.</item>
///          <item><c>StateReset:</c> per invocation — <c>Estimate</c> is a pure character scan
///          and does not touch the tracker's running totals, so nothing accumulates between
///          iterations.</item>
///          <item><c>Drain:</c> none — synchronous.</item>
///          <item><c>RetainedState:</c> the shared <see cref="TokenTracker" /> instance, whose
///          usage counters stay at zero on this path (only <c>RecordTurnUsage</c> would move
///          them).</item>
///          <item><c>AwaitSemantics:</c> n/a — no async in any row.</item>
///          <item><c>AllocAttribution:</c> zero for every row — that is the claim under test.
///          A non-zero allocation here means the estimator regressed to a regex or a substring
///          path.</item>
/// </list>
[MemoryDiagnoser]
[SimpleJob(warmupCount: 3, iterationCount: 5)]
public class TokenEstimatorBenchmark
{
    private AssistantMessage _assistantMessage = null!;
    private ITokenTracker _estimator = null!;
    private string _largeText = null!;
    private string _mediumText = null!;
    private string _mixedText = null!;
    private string _smallText = null!;

    [GlobalSetup]
    public void Setup()
    {
        _estimator = new TokenTracker();
        _smallText = new string('a', 100);
        _mediumText = new string('a', 4_096);
        _largeText = new string('a', 65_536);

        // Mixed CJK + ASCII (~50/50)
        char[] mixed = new char[2_048];
        for (int i = 0; i < mixed.Length; i++)
        {
            mixed[i] = i % 2 == 0 ? 'a' : (char)0x4E2D; // '中'
        }
        _mixedText = new string(mixed);

        _assistantMessage = new AssistantMessage(
            "msg-1",
            "session-1",
            DateTimeOffset.UtcNow,
            new ContentPart[]
            {
                new TextPart(_mediumText),
                new TextPart("Second part for multi-part estimation.")
            },
            StopReason.Stop,
            new Usage(0, 0),
            "stub-1");
    }

    [Benchmark(Description = "Estimate small (100 chars)", Baseline = true)]
    public int Estimate_Small() => _estimator.Estimate(_smallText);

    [Benchmark(Description = "Estimate medium (4K chars)")]
    public int Estimate_Medium() => _estimator.Estimate(_mediumText);

    [Benchmark(Description = "Estimate large (64K chars)")]
    public int Estimate_Large() => _estimator.Estimate(_largeText);

    [Benchmark(Description = "Estimate mixed CJK+ASCII (2K chars)")]
    public int Estimate_Mixed() => _estimator.Estimate(_mixedText);

    [Benchmark(Description = "EstimateMessage (assistant, 2 parts)")]
    public int EstimateMessage() => _estimator.EstimateMessage(_assistantMessage);
}
