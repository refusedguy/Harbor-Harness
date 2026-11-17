using Harbor.Tui.CellForge.Widgets;

namespace Harbor.Tui.CellForge.Tests;

/// <summary>
/// Fuzzy matcher contract for the command palette: subsequence matching,
/// boundary/consecutive bonuses, stable ranking, empty-query passthrough.
/// </summary>
/// <remarks>
/// <para>
/// <b>The bare <c>[NotInParallel]</c> is load-bearing, and the keyed form it
/// replaces could not do the job.</b> The score cache is a process-global LRU
/// inside <see cref="FuzzyMatcher" />, and three of the tests below assert on
/// its SIZE. A named key is a mutex over same-key peers only — see
/// <c>docs/TEST_PATTERNS.md</c>, "What each <c>[NotInParallel]</c> form
/// actually guarantees" — so the <c>("fuzzy-cache")</c> key this class used to
/// carry excluded nothing that matters: the command palette, the file picker
/// and the autocomplete palette all score through the same static cache, and
/// their tests carry no key at all. On the dev run 39724342525 the assertion
/// <c>afterFirst == 1</c> read 2 — one entry was ours, the other belonged to a
/// neighbour that scored a candidate in the same millisecond.
/// </para>
/// <para>
/// The competitor set cannot be enumerated (every palette in this assembly
/// writes to the cache), so the decision procedure in
/// <c>docs/TEST_PATTERNS.md</c> picks the BARE form — "completely alone" —
/// the same one <c>UiStageCounterTests</c> and
/// <c>StorePathSkipsMarkdownParseTests</c> already carry for their
/// process-global counters. TUnit runs the global bucket LAST and one test at
/// a time, so these tests are measured against a quiet process, and what they
/// cost the shard is their own duration.
/// </para>
/// <para>
/// The counter assertions are DELTAS — the count before and after the test's
/// own call — so they state the invariant ("a repeat hit adds nothing")
/// instead of an absolute number that only a quiet process could produce.
/// The delta also makes the tests self-checking: a neighbour that slipped into
/// the window shows up as a non-zero delta instead of as a silently passed
/// assertion.
/// </para>
/// </remarks>
[NotInParallel]
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
    public async Task Score_RepeatHit_DoesNotGrowCache()
    {
        FuzzyMatcher.ClearCache();

        int before = FuzzyMatcher.CacheCount;
        int? first = FuzzyMatcher.Score("t5cache-q", "t5cache-candidate");
        int afterFirst = FuzzyMatcher.CacheCount;
        int? second = FuzzyMatcher.Score("t5cache-q", "t5cache-candidate");
        int afterSecond = FuzzyMatcher.CacheCount;

        await Assert.That(second).IsEqualTo(first);
        await Assert.That(afterFirst - before)
            .IsEqualTo(1)
            .Because("the first hit must cache exactly one (query, candidate) pair");
        await Assert.That(afterSecond - afterFirst)
            .IsEqualTo(0)
            .Because("a repeat hit must be served from the cache, not add a second entry");
    }

    [Test]
    public async Task Score_Miss_IsCachedAsNull()
    {
        FuzzyMatcher.ClearCache();

        int before = FuzzyMatcher.CacheCount;
        int? first = FuzzyMatcher.Score("zzz", "abc");
        int afterFirst = FuzzyMatcher.CacheCount;
        int? second = FuzzyMatcher.Score("zzz", "abc");
        int afterSecond = FuzzyMatcher.CacheCount;

        await Assert.That(first).IsNull();
        await Assert.That(second).IsNull();
        await Assert.That(afterFirst - before)
            .IsEqualTo(1)
            .Because("a miss is cached too: one (query, candidate) pair, one entry");
        await Assert.That(afterSecond - afterFirst)
            .IsEqualTo(0)
            .Because("the cached miss must be served, not re-inserted");
    }

    [Test]
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
        int before = FuzzyMatcher.CacheCount;
        List<string> first = FuzzyMatcher.Filter("ses", catalog, static s => s);
        int afterFirst = FuzzyMatcher.CacheCount;
        List<string> second = FuzzyMatcher.Filter("ses", catalog, static s => s);
        int afterSecond = FuzzyMatcher.CacheCount;

        await Assert.That(first.Count).IsGreaterThan(0);
        await Assert.That(afterFirst - before)
            .IsGreaterThan(0)
            .Because("the first pass must fill the cache — otherwise the second pass proves nothing");
        await Assert.That(afterSecond - afterFirst)
            .IsEqualTo(0)
            .Because("every Score call on the second pass must be a cache hit");
        await Assert.That(second.Count).IsEqualTo(first.Count);
        await Assert.That(second.SequenceEqual(first)).IsTrue();
    }
}
