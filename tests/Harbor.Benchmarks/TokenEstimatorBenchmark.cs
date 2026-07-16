using BenchmarkDotNet.Attributes;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Sessions;
namespace Harbor.Benchmarks;
/// <summary>
///     Benchmarks <see cref="HeuristicTokenEstimator" /> on text payloads of
///     varying sizes. The estimator counts CJK chars (×0.5 tokens) and
///     non-CJK chars (×0.25 tokens), so English-only inputs are O(n) char
///     scans with no allocations.
/// </summary>
[MemoryDiagnoser]
[SimpleJob(warmupCount: 3, iterationCount: 5)]
public class TokenEstimatorBenchmark
{
    private AssistantMessage _assistantMessage = null!;
    private ITokenEstimator _estimator = null!;
    private string _largeText = null!;
    private string _mediumText = null!;
    private string _mixedText = null!;
    private string _smallText = null!;

    [GlobalSetup]
    public void Setup()
    {
        _estimator = new HeuristicTokenEstimator();
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
