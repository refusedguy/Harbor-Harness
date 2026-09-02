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
