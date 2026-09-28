// WordDiffBoundsTests.cs — #460, the third unbounded allocation the issue names.
//
// WordDiff built its LCS as a full int[a+1, b+1] table from the row being
// diffed, and the row is untrusted LLM tool output: an edit or a patch over a
// minified bundle is one single line, and nothing in the diff format caps its
// length. 10 000 tokens a side is (10001)² ints = 400 MB, built on the diff
// parse path, from input the model was handed.
//
// The fix is two bounds, both in WordDiff:
//
//   • MaxPairableLineChars (4 KiB, pre-existing but previously only enforced by
//     PairRun, not by Segment or the public TryPair) now caps tokenization too.
//   • MaxLcsMatrixCells = 128 × 128 = 16 384 cells = 64 KiB caps the table on
//     its PRODUCT, so a 2 048-token row against a 5-token one still gets a real
//     diff while the square case is refused. 64 KiB is the largest table that
//     still lands below the 85 000-byte LOH threshold, so the worst case is a
//     gen0/gen1 array rather than something parked on the large object heap.

namespace Harbor.Tui.CellForge.Tests;

/// <summary>#460 — the intraline diff refuses to size its work from the row.</summary>
public class WordDiffBoundsTests
{
    /// <summary>A row of <paramref name="tokens" /> single-word tokens.</summary>
    private static string Words(int tokens, char letter = 'a') =>
        string.Join(' ', Enumerable.Repeat(new string(letter, 1), tokens));

    [Test]
    public async Task Segment_TenThousandTokensASide_DoesNotBuildThe400MbTable()
    {
        // The exact shape from the issue: a minified single line, ~10k tokens a
        // side. The old code allocated a 400 MB int[,] here.
        string oldLine = Words(10_000, 'a');
        string newLine = Words(10_000, 'b');

        var sides = WordDiff.Segment(oldLine, newLine);

        // Bounded and immediate: no table, and the pair degrades to the same
        // picture an unpaired line-level row paints.
        await Assert.That(sides.Removed.Count).IsEqualTo(1);
        await Assert.That(sides.Inserted.Count).IsEqualTo(1);
        await Assert.That(sides.Removed[0].Kind).IsEqualTo(WordSegKind.Deleted);
        await Assert.That(sides.Removed[0].Text).IsEqualTo(oldLine);
        await Assert.That(sides.Inserted[0].Kind).IsEqualTo(WordSegKind.Added);
        await Assert.That(sides.Inserted[0].Text).IsEqualTo(newLine);
    }

    [Test]
    public async Task Segment_TokenArrayIsBoundedByTheCharacterCap()
    {
        // MaxPairableLineChars is enforced before tokenizing now, not just by
        // PairRun — so the token arrays are bounded too. 4 KiB of text is at
        // most 2 048 whitespace tokens.
        string huge = new('a', 1024 * 1024);

        var sides = WordDiff.Segment(huge, "one two three");

        await Assert.That(sides.Removed.Count).IsEqualTo(1);
        await Assert.That(sides.Removed[0].Kind).IsEqualTo(WordSegKind.Deleted);
        await Assert.That(sides.Removed[0].Text.Length).IsEqualTo(huge.Length);
        // The small side still diffs normally against the unpaired big one.
        await Assert.That(sides.Inserted.Count).IsEqualTo(1);
        await Assert.That(sides.Inserted[0].Kind).IsEqualTo(WordSegKind.Added);
    }

    [Test]
    public async Task TryPair_MegabyteRows_AreBoundedToo()
    {
        // TryPair is public and used to reach Segment with no cap at all — the
        // entry point the issue's "unbounded" claim actually rode in on.
        var delete = new DiffLine(DiffLineKind.Delete, 1, 0, new string('a', 512 * 1024));
        var add = new DiffLine(DiffLineKind.Add, 0, 1, new string('b', 512 * 1024));

        var pair = WordDiff.TryPair(delete, add);

        await Assert.That(pair).IsNotNull();
        await Assert.That(pair!.Removed.Count).IsEqualTo(1);
        await Assert.That(pair.Removed[0].Kind).IsEqualTo(WordSegKind.Deleted);
    }

    [Test]
    public async Task PairRun_OversizedPair_LeavesTheRowsUnpaired()
    {
        // Inside the character cap on both sides, over the matrix cap. The pair
        // must not be emitted at all rather than emitted with an empty context
        // run: a segmented row with no Equal segment paints the row accented
        // one character short of its right edge, because PaintSegmented budgets
        // a separator the line painter does not. Unpaired is the identical
        // picture at no cost.
        const int tokens = 200; // 200 × 200 = 40 401 cells > 16 384
        string shared = "shared anchor tokens here";
        string oldRow = shared + " " + Words(tokens - 5, 'p');
        string newRow = shared + " " + Words(tokens - 5, 'q');
        await Assert.That(oldRow.Length).IsLessThan(WordDiff.MaxPairableLineChars);

        IReadOnlyList<DiffLine> lines =
        [
            new(DiffLineKind.Delete, 1, 0, oldRow),
            new(DiffLineKind.Add, 0, 1, newRow),
        ];

        await Assert.That(WordDiff.PairRun(lines, 0)).IsEmpty();
    }

    [Test]
    public async Task PairRun_WideButLopsidedPair_StillDiffs()
    {
        // The cap is on the PRODUCT, not on either side — which is the whole
        // reason it is not simply "128 tokens a side". 2 005 tokens against 5
        // is 2 006 × 6 = 12 036 cells, inside the 16 384 budget, so this pair
        // gets a real word diff. A per-side cap would have thrown it away, and
        // it is not a contrived shape: it is a generated row next to a one-line
        // replacement.
        const string shared = "keep these five words";
        string longRow = shared + " " + string.Join(' ', Enumerable.Repeat("x", 2_000));
        string shortRow = shared;

        await Assert.That(longRow.Length).IsLessThan(WordDiff.MaxPairableLineChars);

        IReadOnlyList<DiffLine> lines =
        [
            new(DiffLineKind.Delete, 1, 0, longRow),
            new(DiffLineKind.Add, 0, 1, shortRow),
        ];

        var pairs = WordDiff.PairRun(lines, 0);

        await Assert.That(pairs.Count).IsEqualTo(1);

        // The shared words are real anchors, i.e. the table was built and
        // walked — not just that a pair came out.
        var anchor = pairs[0].Sides.Removed.Single(s => s.Kind == WordSegKind.Equal);
        await Assert.That(anchor.Text).IsEqualTo(shared);

        // And the 2 000 tokens that were dropped from the new row show up as
        // one deleted run, which is the only sane way to paint them.
        await Assert.That(pairs[0].Sides.Removed.Count(s => s.Kind == WordSegKind.Deleted)).IsEqualTo(1);
    }

    [Test]
    public async Task PairRun_LopsidedPairJustOverTheProductCap_IsLeftUnpaired()
    {
        // The other side of the same boundary, and the case that actually needed
        // thought: the character cap and the cell cap are DIFFERENT axes, and
        // the natural fixture straddles both. 2 800 filler tokens is 5 562 chars
        // — past MaxPairableLineChars, so PairRun would decline it on the wrong
        // cap and the test would prove nothing.
        //
        // Single-character tokens on both sides separate the axes: 2 802 tokens
        // is 2 803 cells, and 5 603 chars. Against 6 tokens that is
        // 2 803 × 7 = 19 621 cells — over the 16 384 cap, under the 4 KiB
        // character cap. Over the cells, inside the characters, which is the
        // only place a product cap can be tested.
        string longRow = string.Join(' ', Enumerable.Repeat("x", 2_802));
        string shortRow = string.Join(' ', Enumerable.Repeat("y", 6));

        await Assert.That(longRow.Length).IsLessThan(WordDiff.MaxPairableLineChars);
        await Assert.That((2_802 + 1) * (6 + 1)).IsGreaterThan(WordDiff.MaxLcsMatrixCells);

        IReadOnlyList<DiffLine> lines =
        [
            new(DiffLineKind.Delete, 1, 0, longRow),
            new(DiffLineKind.Add, 0, 1, shortRow),
        ];

        await Assert.That(WordDiff.PairRun(lines, 0)).IsEmpty();
    }

    [Test]
    public async Task Segment_InsideTheBudget_StillProducesTheSameDiff()
    {
        // The bound must not have cost anything: a pair just under the cap
        // still anchors on its shared words and still reports the change.
        int tokens = 100; // 101 × 101 = 10 201 cells, well inside 16 384
        string oldRow = string.Join(' ', Enumerable.Repeat("word", tokens));
        string newRow = string.Join(' ', Enumerable.Repeat("word", tokens - 1)) + " tail";

        var sides = WordDiff.Segment(oldRow, newRow);

        await Assert.That(sides.Removed.Any(s => s.Kind == WordSegKind.Equal)).IsTrue();
        await Assert.That(sides.Inserted.Any(s => s.Kind == WordSegKind.Added)).IsTrue();

        string inserted = string.Join(' ', sides.Inserted.Select(s => s.Text));
        await Assert.That(inserted).IsEqualTo(newRow);
    }
}
