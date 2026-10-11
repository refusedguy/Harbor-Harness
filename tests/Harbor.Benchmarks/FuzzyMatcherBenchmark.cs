using BenchmarkDotNet.Attributes;
using Harbor.Tui.CellForge.Widgets;

namespace Harbor.Benchmarks;

/// <summary>
/// <see cref="FuzzyMatcher" /> palette costs on a 3000-item catalog
/// (under the 4096-entry LRU capacity, so the warm row is pure cache hits):
/// cold full-catalog <c>Filter</c> vs warm re-filter with the same query,
/// plus a single cached <c>Score</c> hit. The cold→warm delta is the
/// steal-T5 win (textual <c>fuzzy.py</c>: (query, candidate) LRU4096 +
/// substring fast-path) on the repeated-refilter path every keystroke drives.
/// </summary>
/// <para><b>Measurement contract (#408)</b> — what these numbers include:</para>
/// <list type="bullet">
/// <item><c>Operation:</c> one <c>Filter</c> over the 3000-item catalog with
/// query <c>"ses"</c>, or one cached <c>Score</c> call.</item>
/// <item><c>Payload:</c> deterministic catalog — every 3rd entry is a
/// <c>session-NNNN fork</c> match, the rest are <c>cmd-NNNN run</c> misses;
/// both scoring paths (substring fast-path hits, greedy misses) are exercised.</item>
/// <item><c>StateReset:</c> <c>IterationSetup</c> clears the cache for the cold
/// row (prices a full rescan) and pre-warms it for the warm rows (prices the
/// steady-state repeated keystroke).</item>
/// <item><c>Drain:</c> none — every row is synchronous and returns before the op ends.</item>
/// <item><c>RetainedState:</c> the warm rows intentionally reuse the shared
/// static LRU across iterations — that memoisation is the thing being
/// measured, so it is NOT reset.</item>
/// <item><c>AwaitSemantics:</c> n/a — none of the rows is async.</item>
/// <item><c>AllocAttribution:</c> the cold row allocates the result/score
/// lists plus one cache node per entry; the warm rows allocate only the
/// result/score lists (score lookup is a struct-key dictionary hit).</item>
/// </list>
[MemoryDiagnoser]
[SimpleJob(warmupCount: 3, iterationCount: 5)]
public class FuzzyMatcherBenchmark
{
    private const string Query = "ses";
    private const string HitCandidate = "session-1234 fork";

    private List<string> _catalog = null!;

    [GlobalSetup]
    public void Setup()
    {
        _catalog = new List<string>(3000);
        for (int i = 0; i < 3000; i++)
        {
            _catalog.Add(i % 3 == 0 ? $"session-{i:0000} fork" : $"cmd-{i:0000} run");
        }
    }

    [IterationSetup(Target = nameof(Filter_ColdCatalog))]
    public void ColdSetup() => FuzzyMatcher.ClearCache();

    [Benchmark(Description = "Filter 3000-item catalog, cold cache")]
    public int Filter_ColdCatalog() => FuzzyMatcher.Filter(Query, _catalog, static s => s).Count;

    [IterationSetup(Targets = [nameof(Filter_WarmCatalog), nameof(Score_CachedHit)])]
    public void WarmSetup()
    {
        FuzzyMatcher.ClearCache();
        FuzzyMatcher.Filter(Query, _catalog, static s => s);
        FuzzyMatcher.Score(Query, HitCandidate);
    }

    [Benchmark(Description = "Filter 3000-item catalog, warm cache")]
    public int Filter_WarmCatalog() => FuzzyMatcher.Filter(Query, _catalog, static s => s).Count;

    [Benchmark(Description = "Single Score call, cached hit")]
    public int? Score_CachedHit() => FuzzyMatcher.Score(Query, HitCandidate);
}
