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
}
