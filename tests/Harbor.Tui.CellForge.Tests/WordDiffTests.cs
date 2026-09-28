using System.Globalization;
using Harbor.Tui.CellForge.Widgets;

namespace Harbor.Tui.CellForge.Tests;

public class WordDiffTests
{
    private static string Render(IReadOnlyList<WordSeg> segs, WordSegKind kind) =>
        string.Join(" ", segs.Where(s => s.Kind == kind).Select(s => s.Text));

    private static int WordCount(IReadOnlyList<WordSeg> segs) =>
        segs.Sum(s => s.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length);

    /// <summary>LCS invariant: each side reconstructs its source line.</summary>
    private static async Task AssertReconstructs(string oldLine, string newLine, WordDiffSides sides)
    {
        int oldTokens = oldLine.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
        int newTokens = newLine.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;

        await Assert.That(WordCount(sides.Removed)).IsEqualTo(oldTokens);
        await Assert.That(WordCount(sides.Inserted)).IsEqualTo(newTokens);

        foreach (var seg in sides.Removed.Concat(sides.Inserted))
        {
            await Assert.That(seg.Text.Length > 0).IsTrue();
        }
    }

    [Test]
    public async Task IdenticalLines_SingleEqualRun()
    {
        var sides = WordDiff.Segment("harbor build release", "harbor build release");

        await Assert.That(sides.Removed.Count).IsEqualTo(1);
        await Assert.That(sides.Inserted.Count).IsEqualTo(1);
        await Assert.That(sides.Removed[0].Kind).IsEqualTo(WordSegKind.Equal);
        await Assert.That(sides.Removed[0].Text).IsEqualTo("harbor build release");
    }

    [Test]
    public async Task SingleWordReplacement_ContextReadsNaturally()
    {
        const string oldLine = "the quick brown fox";
        const string newLine = "the slow brown fox";
        var sides = WordDiff.Segment(oldLine, newLine);
        await AssertReconstructs(oldLine, newLine, sides);

        // The replacement is reported exactly once per side…
        await Assert.That(Render(sides.Removed, WordSegKind.Deleted)).IsEqualTo("quick");
        await Assert.That(Render(sides.Inserted, WordSegKind.Added)).IsEqualTo("slow");

        // …and the removed row keeps source order: the(quick)brown fox.
        string removedAll = string.Join(' ', sides.Removed.Select(s => s.Text));
        await Assert.That(removedAll).IsEqualTo("the quick brown fox");

        string insertedAll = string.Join(' ', sides.Inserted.Select(s => s.Text));
        await Assert.That(insertedAll).IsEqualTo("the slow brown fox");
    }

    [Test]
    public async Task PureAddition_PureDeletion()
    {
        var add = WordDiff.Segment(string.Empty, "new words");
        await Assert.That(add.Removed.Count).IsEqualTo(0);
        await Assert.That(add.Inserted.All(s => s.Kind == WordSegKind.Added)).IsTrue();
        await Assert.That(Render(add.Inserted, WordSegKind.Added)).IsEqualTo("new words");

        var del = WordDiff.Segment("old words", string.Empty);
        await Assert.That(del.Inserted.Count).IsEqualTo(0);
        await Assert.That(del.Removed.All(s => s.Kind == WordSegKind.Deleted)).IsTrue();

        var nothing = WordDiff.Segment(string.Empty, string.Empty);
        await Assert.That(nothing.Removed.Count).IsEqualTo(0);
        await Assert.That(nothing.Inserted.Count).IsEqualTo(0);
    }

    [Test]
    public async Task InsertionWithinLine_MarksAddedRunOnly()
    {
        const string oldLine = "git commit";
        const string newLine = "git commit --amend";
        var sides = WordDiff.Segment(oldLine, newLine);
        await AssertReconstructs(oldLine, newLine, sides);

        await Assert.That(sides.Removed.All(s => s.Kind == WordSegKind.Equal)).IsTrue();
        await Assert.That(Render(sides.Inserted, WordSegKind.Added)).IsEqualTo("--amend");

        string insertedAll = string.Join(' ', sides.Inserted.Select(s => s.Text));
        await Assert.That(insertedAll).IsEqualTo(newLine);
    }

    [Test]
    public async Task DeletionWithinLine_MarksDeletedRunOnly()
    {
        const string oldLine = "git commit --amend";
        const string newLine = "git commit";
        var sides = WordDiff.Segment(oldLine, newLine);
        await AssertReconstructs(oldLine, newLine, sides);

        await Assert.That(sides.Inserted.All(s => s.Kind == WordSegKind.Equal)).IsTrue();
        await Assert.That(Render(sides.Removed, WordSegKind.Deleted)).IsEqualTo("--amend");

        string removedAll = string.Join(' ', sides.Removed.Select(s => s.Text));
        await Assert.That(removedAll).IsEqualTo(oldLine);
    }

    [Test]
    public async Task Reordering_ReportsMinimalChanges()
    {
        var sides = WordDiff.Segment("b a", "a b");

        int gapRuns = sides.Removed.Count(s => s.Kind != WordSegKind.Equal)
                    + sides.Inserted.Count(s => s.Kind != WordSegKind.Equal);
        await Assert.That(gapRuns).IsLessThanOrEqualTo(2);
        await Assert.That(WordCount(sides.Removed.Where(s => s.Kind == WordSegKind.Equal).ToList()))
            .IsGreaterThanOrEqualTo(1);
    }

    [Test]
    public async Task TryPair_ValidatesKinds()
    {
        var del = new DiffLine(DiffLineKind.Delete, 3, 0, "content old");
        var add = new DiffLine(DiffLineKind.Add, 0, 4, "content new");
        var ctx = new DiffLine(DiffLineKind.Context, 5, 5, "content");

        var pair = WordDiff.TryPair(del, add);
        await Assert.That(pair).IsNotNull();
        await Assert.That(WordDiff.TryPair(ctx, add)!).IsNull();
        await Assert.That(WordDiff.TryPair(del, ctx)!).IsNull();

        await AssertReconstructs("content old", "content new", pair!);
        await Assert.That(Render(pair!.Removed, WordSegKind.Equal)).Contains("content");
        await Assert.That(Render(pair!.Removed, WordSegKind.Deleted)).Contains("old");
        await Assert.That(Render(pair!.Inserted, WordSegKind.Added)).Contains("new");
    }
}

/// <summary>
/// N:M delete→add run pairing (#380): the similarity metric, the
/// pair-or-fall-back threshold, and the guardrails.
/// </summary>
public class WordDiffPairRunTests
{
    private static string Render(IReadOnlyList<WordSeg> segs, WordSegKind kind) =>
        string.Join(" ", segs.Where(s => s.Kind == kind).Select(s => s.Text));

    /// <summary>
    /// Parses a body-only diff and drops the hunk header, so the returned
    /// indexes are body-row indexes — the ones <c>PairRun</c> reports.
    /// </summary>
    private static IReadOnlyList<DiffLine> Body(params string[] rows) =>
        [.. UnifiedDiffParser.Parse("@@ -1,1 +1,1 @@\n" + string.Join('\n', rows) + "\n").Skip(1)];

    private static string[] Shape(IReadOnlyList<WordPair> pairs) =>
        [.. pairs.OrderBy(p => p.DeleteIndex).Select(p => $"{p.DeleteIndex}->{p.AddIndex}")];

    [Test]
    public async Task Similarity_IdenticalIsHundredAndDisjointIsZero()
    {
        await Assert.That(WordDiff.SimilarityPercent("alpha beta gamma", "alpha beta gamma")).IsEqualTo(100);
        await Assert.That(WordDiff.SimilarityPercent("alpha beta", "delta epsilon")).IsEqualTo(0);
    }

    [Test]
    public async Task Similarity_RepeatedTokensCountOnceEach()
    {
        // "x" x3 vs "x" x1: the multiset overlap is 1, not 3, so the
        // coefficient is 200*1/4 rather than 200*3/4.
        await Assert.That(WordDiff.SimilarityPercent("x x x", "x")).IsEqualTo(50);
    }

    [Test]
    public async Task Similarity_OrderInsensitive()
    {
        // A reorder is still the same line.
        await Assert.That(WordDiff.SimilarityPercent("alpha beta gamma", "gamma beta alpha")).IsEqualTo(100);
    }

    [Test]
    public async Task Similarity_EmptySidesScoreZero()
    {
        // "no tokens" must not read as "identical rewrite".
        await Assert.That(WordDiff.SimilarityPercent(string.Empty, string.Empty)).IsEqualTo(0);
        await Assert.That(WordDiff.SimilarityPercent("alpha", string.Empty)).IsEqualTo(0);
    }

    [Test]
    public async Task Similarity_IgnoresCurrentCulture()
    {
        // Token comparison is ordinal: a culture with different casing rules
        // must not move the score.
        int invariant = WordDiff.SimilarityPercent("FILE file", "file FILE");

        var previous = CultureInfo.CurrentCulture;
        int turkish;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("tr-TR");
            turkish = WordDiff.SimilarityPercent("FILE file", "file FILE");
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }

        await Assert.That(turkish).IsEqualTo(invariant);
    }

    [Test]
    public async Task PairRun_OneToOne_PairsAdjacentRows()
    {
        // The shipped single-replace behaviour: 1:1 pairs even when unrelated.
        var lines = Body("-old line alpha", "+new line beta");

        await Assert.That(Shape(WordDiff.PairRun(lines, 0))).IsEquivalentTo(new[] { "0->1" });
    }

    [Test]
    public async Task PairRun_TwoToThree_PairsBothDeletesToClosestAdds()
    {
        // Rows 0..1 delete, 2..4 add. "port" tracks "port" and "listen"
        // tracks "listen"; "var bind = host" is new and stays unpaired.
        var lines = Body(
            "-var port = 8080",
            "-var listen = host",
            "+var port = 9090",
            "+var bind = host",
            "+var listen = host");

        var pairs = WordDiff.PairRun(lines, 0);

        await Assert.That(Shape(pairs)).IsEquivalentTo(new[] { "0->2", "1->4" });

        var portPair = pairs.Single(p => p.DeleteIndex == 0);
        await Assert.That(Render(portPair.Sides.Inserted, WordSegKind.Equal)).IsEqualTo("var port =");
        await Assert.That(Render(portPair.Sides.Inserted, WordSegKind.Added)).IsEqualTo("9090");
    }

    [Test]
    public async Task PairRun_ThreeToTwo_PairsBothAddsToClosestDeletes()
    {
        var lines = Body(
            "-var port = 8080",
            "-var host = localhost",
            "-var listen = any",
            "+var port = 9090",
            "+var host = 0.0.0.0");

        await Assert.That(Shape(WordDiff.PairRun(lines, 0))).IsEquivalentTo(new[] { "0->3", "1->4" });
    }

    [Test]
    public async Task PairRun_InsertOnlyAndDeleteOnly_ProduceNoPairs()
    {
        await Assert.That(WordDiff.PairRun(Body("+first added", "+second added"), 0)).IsEmpty();
        await Assert.That(WordDiff.PairRun(Body("-first removed", "-second removed"), 0)).IsEmpty();
    }

    [Test]
    public async Task PairRun_TwoToTwo_PairsBothRowsWhenSimilar()
    {
        // 2:2 is still a guess about correspondence, so it must earn the
        // threshold like any other N:M shape — here it does.
        var lines = Body(
            "-var port = 8080",
            "-var host = localhost",
            "+var port = 9090",
            "+var host = 0.0.0.0");

        await Assert.That(Shape(WordDiff.PairRun(lines, 0))).IsEquivalentTo(new[] { "0->2", "1->3" });
    }

    [Test]
    public async Task PairRun_UnrelatedRewrite_FallsBackToLineLevel()
    {
        // Nothing in common on either side: no pair clears the threshold, so
        // the run stays plain line-level rows instead of being rainbowed.
        var lines = Body(
            "-zzz qqq xxx",
            "-www vvv uuu",
            "+aaa bbb ccc",
            "+ddd eee fff");

        await Assert.That(WordDiff.PairRun(lines, 0)).IsEmpty();
    }

    [Test]
    public async Task PairRun_LeavesUnrelatedRowsUnpairedInsideRelatedRun()
    {
        // Two deletes rewritten into three adds, where only one add is a
        // genuine rewrite of one delete; the noise rows on both sides get
        // no emphasis.
        var lines = Body(
            "-var port = 8080",
            "-completely unrelated text here",
            "+var port = 9090",
            "+alpha beta gamma",
            "+delta epsilon zeta");

        await Assert.That(Shape(WordDiff.PairRun(lines, 0))).IsEquivalentTo(new[] { "0->2" });
    }

    [Test]
    public async Task PairRun_EachRowTakesPartInAtMostOnePair()
    {
        var lines = Body(
            "-var port = 8080",
            "-var host = localhost",
            "-var mode = strict",
            "+var port = 9090",
            "+var host = 0.0.0.0");

        var pairs = WordDiff.PairRun(lines, 0);

        await Assert.That(pairs.Count).IsEqualTo(2);
        await Assert.That(pairs.Select(p => p.DeleteIndex).Distinct().Count()).IsEqualTo(pairs.Count);
        await Assert.That(pairs.Select(p => p.AddIndex).Distinct().Count()).IsEqualTo(pairs.Count);
    }

    [Test]
    public async Task PairRun_ContextRowEndsTheRun()
    {
        // A context row between the runs makes the adds a pure insert.
        var lines = Body("-var port = 8080", " unchanged context", "+var port = 9090");

        await Assert.That(WordDiff.PairRun(lines, 0)).IsEmpty();
    }

    [Test]
    public async Task PairRun_NonDeleteOrOutOfRangeStart_IsEmpty()
    {
        var lines = Body(" unchanged", "-removed", "+added");

        await Assert.That(WordDiff.PairRun(lines, 0)).IsEmpty();
        await Assert.That(WordDiff.PairRun(lines, -1)).IsEmpty();
        await Assert.That(WordDiff.PairRun(lines, 99)).IsEmpty();
    }

    [Test]
    public async Task PairRun_SingleLineOver4Kb_SkipsPairing()
    {
        // Guardrail: the O(tokens²) LCS matrix must never be built for a
        // minified megabyte-scale row.
        var huge = new string('a', WordDiff.MaxPairableLineChars + 1);
        var lines = Body($"-{huge}", "+totally different text", "+one more line");

        await Assert.That(WordDiff.PairRun(lines, 0)).IsEmpty();
    }

    [Test]
    public async Task PairRun_RowExactlyAt4Kb_StillPairs()
    {
        // The guardrail is a ceiling, not a heuristic that eats the last
        // legal row.
        const string shared = "shared prefix tokens here";
        int pad = WordDiff.MaxPairableLineChars - shared.Length - 1;
        string original = shared + " " + new string('p', pad);
        string rewritten = shared + " " + new string('q', pad);

        await Assert.That(original.Length).IsEqualTo(WordDiff.MaxPairableLineChars);
        await Assert.That(WordDiff.PairRun(Body($"-{original}", $"+{rewritten}"), 0).Count).IsEqualTo(1);
    }

    [Test]
    public async Task PairRun_IsDeterministicAcrossRepeatedCalls()
    {
        var lines = Body(
            "-var port = 8080",
            "-var host = localhost",
            "-var mode = strict",
            "+var port = 9090",
            "+var host = 0.0.0.0",
            "+var mode = relaxed",
            "+var extra = 1");

        string[] first = Shape(WordDiff.PairRun(lines, 0));

        await Assert.That(Shape(WordDiff.PairRun(lines, 0))).IsEquivalentTo(first);
        await Assert.That(Shape(WordDiff.PairRun(lines, 0))).IsEquivalentTo(first);
    }

    [Test]
    public async Task PairRun_EqualScoresKeepTheEarlierDeleteRow()
    {
        // Both deletes score identically against the single add; the tie must
        // resolve to the earlier row, not to whichever was scanned last.
        var lines = Body("-alpha beta", "-gamma delta", "+alpha gamma");

        await Assert.That(Shape(WordDiff.PairRun(lines, 0))).IsEquivalentTo(new[] { "0->2" });
    }
}
