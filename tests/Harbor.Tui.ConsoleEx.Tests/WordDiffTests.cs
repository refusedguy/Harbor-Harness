using Harbor.Tui.ConsoleEx.Widgets;

namespace Harbor.Tui.ConsoleEx.Tests;

public class WordDiffTests
{
    private static string Render(IReadOnlyList<WordSeg> segs, WordSegKind kind) =>
        string.Join(" ", segs.Where(s => s.Kind == kind).Select(s => s.Text));

    private static int WordCount(IReadOnlyList<WordSeg> segs) =>
        segs.Sum(s => s.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length);

    /// <summary>LCS invariant: shared+sides reconstruct both inputs.</summary>
    private static async Task AssertReconstructs(string oldLine, string newLine, IReadOnlyList<WordSeg> segs)
    {
        int oldTokens = oldLine.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
        int newTokens = newLine.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;

        await Assert.That(WordCount(segs.Where(s => s.Kind != WordSegKind.Added).ToList())).IsEqualTo(oldTokens);
        await Assert.That(WordCount(segs.Where(s => s.Kind != WordSegKind.Deleted).ToList())).IsEqualTo(newTokens);

        foreach (var seg in segs)
        {
            await Assert.That(seg.Text.Length > 0).IsTrue();
        }
    }

    [Test]
    public async Task IdenticalLines_SingleEqualRun()
    {
        var segs = WordDiff.Segment("harbor build release", "harbor build release");

        await Assert.That(segs.Count).IsEqualTo(1);
        await Assert.That(segs[0].Kind).IsEqualTo(WordSegKind.Equal);
        await Assert.That(segs[0].Text).IsEqualTo("harbor build release");
    }

    [Test]
    public async Task SingleWordReplacement_SplitsAroundChangedToken()
    {
        var segs = WordDiff.Segment("the quick brown fox", "the slow brown fox");
        AssertReconstructs("the quick brown fox", "the slow brown fox", segs);

        // The replacement itself is reported exactly once per side…
        await Assert.That(Render(segs, WordSegKind.Deleted)).IsEqualTo("quick");
        await Assert.That(Render(segs, WordSegKind.Added)).IsEqualTo("slow");

        // …and all context survives on the Equal side, in order.
        string equal = Render(segs, WordSegKind.Equal);
        await Assert.That(equal.IndexOf("the")).IsGreaterThanOrEqualTo(0);
        await Assert.That(equal.IndexOf("brown")).IsGreaterThan(equal.IndexOf("the"));
        await Assert.That(equal.IndexOf("fox")).IsGreaterThan(equal.IndexOf("brown"));
    }

    [Test]
    public async Task PureAddition_PureDeletion()
    {
        var add = WordDiff.Segment(string.Empty, "new words");
        await Assert.That(add.All(s => s.Kind == WordSegKind.Added)).IsTrue();
        await Assert.That(Render(add, WordSegKind.Added)).IsEqualTo("new words");

        var del = WordDiff.Segment("old words", string.Empty);
        await Assert.That(del.All(s => s.Kind == WordSegKind.Deleted)).IsTrue();
        await Assert.That(Render(del, WordSegKind.Deleted)).IsEqualTo("old words");

        var nothing = WordDiff.Segment(string.Empty, string.Empty);
        await Assert.That(nothing.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Reordering_ReportsMinimalChanges()
    {
        var segs = WordDiff.Segment("b a", "a b");

        int deletedWords = segs.Count(s => s.Kind == WordSegKind.Deleted);
        int addedWords = segs.Count(s => s.Kind == WordSegKind.Added);
        // Only ONE word can move net-per-side in a 2-token swap.
        await Assert.That(deletedWords + addedWords).IsLessThanOrEqualTo(2);
        await Assert.That(WordCount(segs.Where(s => s.Kind == WordSegKind.Equal).ToList())).IsGreaterThanOrEqualTo(1);
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
        await Assert.That(Render(pair!, WordSegKind.Equal)).Contains("content");
        await Assert.That(Render(pair!, WordSegKind.Deleted)).Contains("old");
        await Assert.That(Render(pair!, WordSegKind.Added)).Contains("new");
    }
}
