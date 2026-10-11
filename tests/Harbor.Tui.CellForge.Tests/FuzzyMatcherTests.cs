using Harbor.Tui.CellForge.Widgets;

namespace Harbor.Tui.CellForge.Tests;

/// <summary>
/// Fuzzy matcher contract for the command palette: subsequence matching,
/// boundary/consecutive bonuses, stable ranking, empty-query passthrough.
/// </summary>
public class FuzzyMatcherTests
{
    [Test]
    public async Task Score_EmptyQuery_MatchesNeutrally()
    {
        await Assert.That(FuzzyMatcher.Score("", "anything")).IsEqualTo(0);
    }

    [Test]
    public async Task Score_NotASubsequence_ReturnsNull()
    {
        await Assert.That(FuzzyMatcher.Score("xz", "clear session")).IsNull();
        await Assert.That(FuzzyMatcher.Score("zzz", "abcabc")).IsNull();
    }

    [Test]
    public async Task Score_CaseInsensitiveSubsequence_Matches()
    {
        int? score = FuzzyMatcher.Score("cs", "clear session");
        await Assert.That(score).IsNotNull();
    }

    [Test]
    public async Task Score_WordBoundaries_BeatMidWordHits()
    {
        int? boundary = FuzzyMatcher.Score("s", "clear session");
        int? mid = FuzzyMatcher.Score("s", "sessionx");
        int? midLate = FuzzyMatcher.Score("s", "xxxxsx");

        await Assert.That(boundary).IsNotNull();
        await Assert.That(mid).IsNotNull();
        // "clear session": s at index 6 (no boundary), "sessionx": s at 0 (boundary).
        await Assert.That(mid!.Value).IsGreaterThan(boundary!.Value);
        await Assert.That(mid.Value).IsGreaterThan(midLate!.Value); // early hits rank higher
    }

    [Test]
    public async Task Score_ConsecutiveHits_BeatSparseOnSameCandidate()
    {
        int? consecutive = FuzzyMatcher.Score("ses", "session");
        int? sparse = FuzzyMatcher.Score("sen", "session"); // same prefix, then a jump

        await Assert.That(consecutive!.Value).IsGreaterThan(sparse!.Value);
    }

    [Test]
    public async Task Score_CamelCaseHump_IsBoundary()
    {
        int? hump = FuzzyMatcher.Score("t", "newTool");
        await Assert.That(hump).IsNotNull();
        await Assert.That(hump!.Value).IsGreaterThan(0); // boundary + early → positive
    }

    [Test]
    public async Task Filter_KeepsOnlyMatches_AndRanksBestFirst()
    {
        var items = new[] { "quit session", "clear", "session fork", "help" };
        List<string> ranked = FuzzyMatcher.Filter("ses", items, static s => s);

        await Assert.That(ranked).Count().IsEqualTo(2);
        await Assert.That(ranked[0]).IsEqualTo("session fork"); // boundary hit beats mid-word
    }

    [Test]
    public async Task Filter_EmptyQuery_ReturnsAllInOrder()
    {
        var items = new[] { "b", "a", "c" };
        List<string> ranked = FuzzyMatcher.Filter("", items, static s => s);

        await Assert.That(ranked).IsEquivalentTo(["b", "a", "c"]);
    }

    [Test]
    public async Task Filter_EqualScores_StableByInputOrder()
    {
        var items = new[] { "aa", "ab" };
        List<string> ranked = FuzzyMatcher.Filter("a", items, static s => s);

        await Assert.That(ranked[0]).IsEqualTo("aa");
        await Assert.That(ranked[1]).IsEqualTo("ab");
    }

    [Test]
    public async Task Score_SubstringFastPath_BeatsScatteredSubsequence()
    {
        int? contiguous = FuzzyMatcher.Score("ses", "xxsesxx"); // substring → ×1.5
        int? scattered = FuzzyMatcher.Score("ses", "sxxexxs"); // subsequence, 3 groups

        await Assert.That(contiguous).IsNotNull();
        await Assert.That(scattered).IsNotNull();
        await Assert.That(contiguous!.Value).IsGreaterThan(scattered!.Value);
    }

    [Test]
    public async Task Score_ExactMatch_BeatsSubstring()
    {
        int? exact = FuzzyMatcher.Score("abc", "abc"); // ×2.0
        int? prefix = FuzzyMatcher.Score("abc", "abcx"); // ×1.5

        await Assert.That(exact!.Value).IsGreaterThan(prefix!.Value);
    }

    [Test]
    public async Task Score_FirstLetterAndGroupBonus_BeatMidWordSparse()
    {
        // "foo bar": both hits on word starts, 2 groups.
        // "xfoob": no word-start hits, 2 groups, later positions.
        int? wordStarts = FuzzyMatcher.Score("fb", "foo bar");
        int? midWord = FuzzyMatcher.Score("fb", "xfoob");

        await Assert.That(wordStarts).IsNotNull();
        await Assert.That(midWord).IsNotNull();
        await Assert.That(wordStarts!.Value).IsGreaterThan(midWord!.Value);
    }

    [Test]
    public async Task Score_CaseInsensitiveFastPath_SameScore()
    {
        int? lower = FuzzyMatcher.Score("ses", "session");
        int? upper = FuzzyMatcher.Score("SES", "session");

        await Assert.That(lower).IsNotNull();
        await Assert.That(upper).IsEqualTo(lower);
    }

    [Test]
    [NotInParallel("fuzzy-cache")]
    public async Task Score_RepeatHit_DoesNotGrowCache()
    {
        FuzzyMatcher.ClearCache();

        int? first = FuzzyMatcher.Score("t5cache-q", "t5cache-candidate");
        int afterFirst = FuzzyMatcher.CacheCount;
        int? second = FuzzyMatcher.Score("t5cache-q", "t5cache-candidate");

        await Assert.That(second).IsEqualTo(first);
        await Assert.That(afterFirst).IsEqualTo(1);
        await Assert.That(FuzzyMatcher.CacheCount).IsEqualTo(1);
    }

    [Test]
    [NotInParallel("fuzzy-cache")]
    public async Task Score_Miss_IsCachedAsNull()
    {
        FuzzyMatcher.ClearCache();

        int? first = FuzzyMatcher.Score("zzz", "abc");
        int? second = FuzzyMatcher.Score("zzz", "abc");

        await Assert.That(first).IsNull();
        await Assert.That(second).IsNull();
        await Assert.That(FuzzyMatcher.CacheCount).IsEqualTo(1);
    }

    [Test]
    [NotInParallel("fuzzy-cache")]
    public async Task Filter_LargeCatalog_SecondPassUsesCache()
    {
        // Deterministic perf proof for the 4096-entry (query, candidate) LRU:
        // a second identical Filter over a 3000-item catalog (under capacity)
        // must not grow the cache and must rank identically — every Score
        // call on the second pass is a cache hit.
        var catalog = new List<string>(3000);
        for (int i = 0; i < 3000; i++)
        {
            catalog.Add(i % 3 == 0 ? $"session-{i:0000} fork" : $"cmd-{i:0000} run");
        }

        FuzzyMatcher.ClearCache();
        List<string> first = FuzzyMatcher.Filter("ses", catalog, static s => s);
        int afterFirst = FuzzyMatcher.CacheCount;
        List<string> second = FuzzyMatcher.Filter("ses", catalog, static s => s);

        await Assert.That(first.Count).IsGreaterThan(0);
        await Assert.That(FuzzyMatcher.CacheCount).IsEqualTo(afterFirst);
        await Assert.That(second.Count).IsEqualTo(first.Count);
        await Assert.That(second.SequenceEqual(first)).IsTrue();
    }
}
