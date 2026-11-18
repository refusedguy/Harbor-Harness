// WordDiffHighlightTests.cs — K13: word-level inline highlighting in the
// compact diff shown by a tool call card.
//
// These pin the pure projection, not the Avalonia control. The view only maps
// each run's Emphasized flag to a brush; the words that end up emphasized are
// decided here from WordDiff.Segment, the one intraline engine in the repo.

using Harbor.App.Avalonia.ViewModels;
using TUnit;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;

namespace Harbor.App.Avalonia.Tests;

public class WordDiffHighlightTests
{
    [Test]
    public async Task UnpairedLine_IsOneOrdinaryRun()
    {
        IReadOnlyList<WordDiffLine> lines = WordDiffHighlight.Build("  context", 6);

        await Assert.That(lines.Count).IsEqualTo(1);
        await Assert.That(lines[0].RawText).IsEqualTo("  context");
        await Assert.That(lines[0].Runs.Count).IsEqualTo(1);
        await Assert.That(lines[0].Runs[0].Text).IsEqualTo("  context");
        await Assert.That(lines[0].Runs[0].Emphasized).IsFalse();
    }

    [Test]
    public async Task PairedDeleteAdd_EmphasizesOnlyTheChangedWord()
    {
        IReadOnlyList<WordDiffLine> lines =
            WordDiffHighlight.Build("-foo bar baz\n+foo qux baz", 6);

        await Assert.That(lines.Count).IsEqualTo(2);

        string[] removedEmphasis = lines[0].Runs
            .Where(r => r.Emphasized).Select(r => r.Text.Trim()).ToArray();
        string[] addedEmphasis = lines[1].Runs
            .Where(r => r.Emphasized).Select(r => r.Text.Trim()).ToArray();

        await Assert.That(removedEmphasis).IsEquivalentTo(new[] { "bar" });
        await Assert.That(addedEmphasis).IsEquivalentTo(new[] { "qux" });

        string[] removedShared = lines[0].Runs
            .Where(r => !r.Emphasized).Select(r => r.Text.Trim()).ToArray();
        string[] addedShared = lines[1].Runs
            .Where(r => !r.Emphasized).Select(r => r.Text.Trim()).ToArray();

        await Assert.That(removedShared).IsEquivalentTo(new[] { "-", "foo", "baz" });
        await Assert.That(addedShared).IsEquivalentTo(new[] { "+", "foo", "baz" });
    }

    [Test]
    public async Task PairedLine_KeepsEachWordInItsOwnSide()
    {
        IReadOnlyList<WordDiffLine> lines =
            WordDiffHighlight.Build("-one two three\n+one four three", 6);

        await Assert.That(lines[0].Runs.Any(r => r.Emphasized && r.Text.Trim() == "two")).IsTrue();
        await Assert.That(lines[0].Runs.Any(r => r.Emphasized && r.Text.Trim() == "four")).IsFalse();
        await Assert.That(lines[1].Runs.Any(r => r.Emphasized && r.Text.Trim() == "four")).IsTrue();
        await Assert.That(lines[1].Runs.Any(r => r.Emphasized && r.Text.Trim() == "two")).IsFalse();
    }

    [Test]
    public async Task TruncationRow_AppearsAfterTheLineLimit()
    {
        IReadOnlyList<WordDiffLine> lines = WordDiffHighlight.Build("a\nb\nc\nd", 2);

        await Assert.That(lines.Count).IsEqualTo(3);
        await Assert.That(lines[0].RawText).IsEqualTo("a");
        await Assert.That(lines[1].RawText).IsEqualTo("b");
        await Assert.That(lines[2].RawText).IsEqualTo(WordDiffHighlight.TruncationText);
        await Assert.That(lines[2].Runs[0].Emphasized).IsFalse();
    }

    [Test]
    public async Task Pair_NeverExceedsTheLineLimit()
    {
        IReadOnlyList<WordDiffLine> lines =
            WordDiffHighlight.Build("-old value\n+new value\nmore", 2);

        await Assert.That(lines.Count).IsEqualTo(3);
        await Assert.That(lines[2].RawText).IsEqualTo(WordDiffHighlight.TruncationText);
    }

    [Test]
    public async Task CrLfAndHunkHeaders_ArePreserved()
    {
        IReadOnlyList<WordDiffLine> lines =
            WordDiffHighlight.Build("-old value\r\n+new value\r\n@@ -1 +1 @@\r\n", 6);

        await Assert.That(lines[0].RawText).IsEqualTo("-old value");
        await Assert.That(lines[1].RawText).IsEqualTo("+new value");
        await Assert.That(lines[2].RawText).IsEqualTo("@@ -1 +1 @@");
        await Assert.That(lines[2].Runs[0].Emphasized).IsFalse();
    }

    [Test]
    public async Task EmptyDiff_ProducesNoLines()
    {
        await Assert.That(WordDiffHighlight.Build(null, 6)).IsEmpty();
        await Assert.That(WordDiffHighlight.Build(string.Empty, 6)).IsEmpty();
    }
}
