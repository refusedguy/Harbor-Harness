using TUnit.Assertions;
using TUnit.Assertions.Extensions;

namespace Harbor.Tools.Builtin.Tests;

/// <summary>
///     Tests for <see cref="HunkParser" /> in isolation — unified-diff parsing only,
///     no filesystem, no <see cref="PatchTool" /> involved.
/// </summary>
public class HunkParserTests
{
    [Test]
    public async Task Parse_SingleHunk_ReturnsHeaderCountsAndLineKinds()
    {
        const string patch = "@@ -1,3 +1,4 @@\n line1\n-line2\n+line2-new\n+line2-extra\n line3\n";

        var hunks = HunkParser.Parse(patch);

        await Assert.That(hunks.Count).IsEqualTo(1);
        var hunk = hunks[0];
        await Assert.That(hunk.OldStart).IsEqualTo(1);
        await Assert.That(hunk.OldCount).IsEqualTo(3);
        await Assert.That(hunk.NewStart).IsEqualTo(1);
        await Assert.That(hunk.NewCount).IsEqualTo(4);
        await Assert.That(hunk.Lines.Count).IsEqualTo(5);
        await Assert.That(hunk.Lines[0]).IsEqualTo(new HunkLine(HunkLineType.Context, "line1"));
        await Assert.That(hunk.Lines[1]).IsEqualTo(new HunkLine(HunkLineType.Deletion, "line2"));
        await Assert.That(hunk.Lines[2]).IsEqualTo(new HunkLine(HunkLineType.Addition, "line2-new"));
    }

    [Test]
    public async Task Parse_DiffGitPreamble_SkipsToFirstHunk()
    {
        const string patch = "diff --git a/f.txt b/f.txt\n--- a/f.txt\n+++ b/f.txt\n@@ -1,1 +1,1 @@\n-a\n+b\n";

        var hunks = HunkParser.Parse(patch);

        await Assert.That(hunks.Count).IsEqualTo(1);
        await Assert.That(hunks[0].Lines.Count).IsEqualTo(2);
    }

    [Test]
    public async Task Parse_NoHunkHeaders_ReturnsEmpty()
    {
        var hunks = HunkParser.Parse("just some text\nno headers here\n");

        await Assert.That(hunks.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Parse_MultipleHunks_ReturnsAllInOrder()
    {
        const string patch = "@@ -1,1 +1,1 @@\n-a\n+b\n@@ -10,1 +10,1 @@\n-c\n+d\n";

        var hunks = HunkParser.Parse(patch);

        await Assert.That(hunks.Count).IsEqualTo(2);
        await Assert.That(hunks[0].OldStart).IsEqualTo(1);
        await Assert.That(hunks[1].OldStart).IsEqualTo(10);
    }

    [Test]
    public async Task Parse_EmptyLine_TreatedAsBlankContext()
    {
        const string patch = "@@ -1,3 +1,3 @@\n line1\n\n line3\n";

        var hunks = HunkParser.Parse(patch);

        await Assert.That(hunks.Count).IsEqualTo(1);
        await Assert.That(hunks[0].Lines.Count).IsEqualTo(3);
        await Assert.That(hunks[0].Lines[1]).IsEqualTo(new HunkLine(HunkLineType.Context, string.Empty));
    }

    [Test]
    public async Task Parse_NoNewlineMarkerLine_IgnoredWithoutStoppingHunk()
    {
        const string patch = "@@ -1,1 +1,1 @@\n-a\n\\ No newline at end of file\n+b\n@@ -5,1 +5,1 @@\n-c\n+d\n";

        var hunks = HunkParser.Parse(patch);

        await Assert.That(hunks.Count).IsEqualTo(2);
        await Assert.That(hunks[0].Lines.Count).IsEqualTo(2);
    }

    [Test]
    public async Task Parse_MalformedHeader_ThrowsFormatException()
    {
        await Assert.That(async () =>
            {
                HunkParser.Parse("@@ nope\n line\n");
                await Task.CompletedTask;
            })
            .Throws<FormatException>();
    }
}
