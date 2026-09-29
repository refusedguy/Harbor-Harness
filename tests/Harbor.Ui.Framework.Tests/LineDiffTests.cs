using Harbor.Ui.Framework.Rendering.Widgets;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;

namespace Harbor.Ui.Framework.Tests;

/// <summary>
///     Issue #679 — the line-level LCS in the core, and the one diff-format
///     reader/Writer pair around it.
/// </summary>
/// <remarks>
///     The black-box guard lives in <see cref="LineDiffAlignmentGuardTests" />:
///     what the user sees. These tests pin the algorithm underneath, including
///     the two properties that make it safe to point a view-model at — the
///     common region is matched before anything is allocated, and the table is
///     a bounded constant.
/// </remarks>
public class LineDiffTests
{
    // ---------------------------------------------------------------------
    // Compute — the LCS itself.
    // ---------------------------------------------------------------------

    [Test]
    public async Task MidFileInsertion_IsOneAdditionAndNothingElse()
    {
        var rows = LineDiff.Compute("alpha\nbravo\ncharlie", "alpha\nbravo\nINSERTED\ncharlie");

        await Assert.That(rows.Count(r => r.Kind == LineDiffRowKind.Added)).IsEqualTo(1);
        await Assert.That(rows.Count(r => r.Kind == LineDiffRowKind.Removed)).IsEqualTo(0);
        await Assert.That(rows.Count(r => r.Kind == LineDiffRowKind.Unchanged)).IsEqualTo(3);
        await Assert.That(rows[2].Text).IsEqualTo("INSERTED").Because("the addition sits where it was written");
        await Assert.That(rows[2].NewLineNumber).IsEqualTo(3);
    }

    [Test]
    public async Task MidFileInsertion_NumbersLinesOnTheirOwnSide()
    {
        var rows = LineDiff.Compute("alpha\nbravo\ncharlie", "alpha\nbravo\nINSERTED\ncharlie");

        // "charlie" is line 3 before the insertion and line 4 after it. A row
        // that is unchanged on both sides must say so twice.
        var charlie = rows[^1];
        await Assert.That(charlie.Kind).IsEqualTo(LineDiffRowKind.Unchanged);
        await Assert.That(charlie.OldLineNumber).IsEqualTo(3);
        await Assert.That(charlie.NewLineNumber).IsEqualTo(4);
    }

    [Test]
    public async Task ReorderedLines_AreAMoveNotAFullRewrite()
    {
        // The other index-diff casualty: swapping two middle lines used to
        // report BOTH of them as modified. Here one line moves and the other
        // is already matched in place.
        var rows = LineDiff.Compute("a\nb\nc", "a\nc\nb");

        await Assert.That(rows.Count(r => r.Kind == LineDiffRowKind.Unchanged)).IsEqualTo(2);
        await Assert.That(rows.Count(r => r.Kind == LineDiffRowKind.Removed)).IsEqualTo(1);
        await Assert.That(rows.Count(r => r.Kind == LineDiffRowKind.Added)).IsEqualTo(1);
    }

    [Test]
    public async Task IdenticalTexts_AreEveryRowUnchanged()
    {
        var rows = LineDiff.Compute("a\nb\nc", "a\nb\nc");

        await Assert.That(rows.Count).IsEqualTo(3);
        await Assert.That(rows.All(r => r.Kind == LineDiffRowKind.Unchanged)).IsTrue();
    }

    [Test]
    public async Task EveryInputLineIsAccountedForExactlyOnce()
    {
        string oldText = string.Join('\n', ["a", "b", "c", "d", "e"]);
        string newText = string.Join('\n', ["a", "b2", "c", "e"]);

        var rows = LineDiff.Compute(oldText, newText);

        // One row per line of each side, and the old-side numbers are 1..n with
        // no gap and no repeat — the invariant an index diff silently breaks.
        var oldNumbers = rows.Where(r => r.OldLineNumber > 0).Select(r => r.OldLineNumber).ToList();
        var newNumbers = rows.Where(r => r.NewLineNumber > 0).Select(r => r.NewLineNumber).ToList();

        await Assert.That(oldNumbers).IsEquivalentTo(Enumerable.Range(1, 5).ToArray());
        await Assert.That(newNumbers).IsEquivalentTo(Enumerable.Range(1, 4).ToArray());
    }

    // ---------------------------------------------------------------------
    // Compute — the budget, and the trim that keeps ordinary edits inside it.
    // ---------------------------------------------------------------------

    [Test]
    public async Task ALargeFileWithOneInsertedLine_IsStillExactlyOneAddition()
    {
        // 5000 lines, one inserted after the 2500th. The common prefix and
        // suffix are matched before the search starts, so the table is sized by
        // the single changed line — the edit-distance bound must not degrade
        // the most ordinary edit there is.
        var oldText = string.Join('\n', Enumerable.Range(0, 5000).Select(i => $"line {i}"));
        string newText = string.Join(
            '\n',
            Enumerable.Range(0, 2500).Select(i => $"line {i}")
                .Append("INSERTED")
                .Concat(Enumerable.Range(2500, 2500).Select(i => $"line {i}")));

        var rows = LineDiff.Compute(oldText, newText);

        await Assert.That(rows.Count(r => r.Kind == LineDiffRowKind.Added)).IsEqualTo(1);
        await Assert.That(rows.Count(r => r.Kind == LineDiffRowKind.Removed)).IsEqualTo(0);
        await Assert.That(rows.Count(r => r.Kind == LineDiffRowKind.Unchanged)).IsEqualTo(5000);
    }

    [Test]
    public async Task ALargeCoreWithATinyEdit_IsStillExact()
    {
        // The trim alone does not get here: the first line changes too, so the
        // common prefix is empty and the search runs over a 2500 x 2501 core.
        // Myers' cost is the SIZE OF THE CHANGE (two lines), not the product,
        // so this stays exact where an LCS table would have had to allocate
        // 100 MB to answer the same question.
        var oldText = string.Join('\n', Enumerable.Range(0, 5000).Select(i => $"line {i}"));
        var newText = string.Join(
            '\n',
            new[] { "HEADER" }
                .Concat(Enumerable.Range(0, 2500).Select(i => $"line {i}"))
                .Append("INSERTED")
                .Concat(Enumerable.Range(2500, 2500).Select(i => $"line {i}")));

        var rows = LineDiff.Compute(oldText, newText);

        await Assert.That(rows.Count(r => r.Kind == LineDiffRowKind.Removed)).IsEqualTo(0)
            .Because("the first line was not rewritten, it moved down by the inserted header");
        await Assert.That(rows.Count(r => r.Kind == LineDiffRowKind.Added)).IsEqualTo(2)
            .Because("the header and the inserted line, and nothing else");
        await Assert.That(rows.Count(r => r.Kind == LineDiffRowKind.Unchanged)).IsEqualTo(5000)
            .Because("everything else is untouched and must be reported as untouched");
    }

    [Test]
    public async Task ARegionOfFourHundredChangedLines_IsStillSearchedExactly()
    {
        // 200 removals plus 200 additions is an edit distance of 400, inside
        // MaxEditDistance. The bound is about pathological input, not about
        // ordinary hunks.
        var oldText = string.Join('\n', Enumerable.Range(0, 200).Select(i => $"old {i}"));
        var newText = string.Join('\n', Enumerable.Range(0, 200).Select(i => $"new {i}"));

        var rows = LineDiff.Compute(oldText, newText);

        await Assert.That(rows.Count(r => r.Kind == LineDiffRowKind.Unchanged)).IsEqualTo(0)
            .Because("nothing is common, so nothing can be claimed to be matched");
        await Assert.That(rows.Count(r => r.Kind == LineDiffRowKind.Removed)).IsEqualTo(200);
        await Assert.That(rows.Count(r => r.Kind == LineDiffRowKind.Added)).IsEqualTo(200);
        await Assert.That(rows[199].Kind).IsEqualTo(LineDiffRowKind.Removed)
            .Because("the removals come before the additions, which is what the format reads as a rewrite");
    }

    [Test]
    public async Task ARegionPastTheEditDistanceBound_DegradesToARemoveRunThenAnAddRun()
    {
        // 600 lines a side with nothing in common is an edit distance of 1200,
        // past MaxEditDistance, so no anchors are searched for. The result is
        // the coarse picture — a bounded constant of work, not a quadratic
        // table sized by the input.
        var oldText = string.Join('\n', Enumerable.Range(0, 600).Select(i => $"old {i}"));
        var newText = string.Join('\n', Enumerable.Range(0, 600).Select(i => $"new {i}"));

        var rows = LineDiff.Compute(oldText, newText);

        await Assert.That(rows.Count(r => r.Kind == LineDiffRowKind.Unchanged)).IsEqualTo(0)
            .Because("no anchors were affordable, so nothing is claimed to be matched");
        await Assert.That(rows.Count(r => r.Kind == LineDiffRowKind.Removed)).IsEqualTo(600);
        await Assert.That(rows.Count(r => r.Kind == LineDiffRowKind.Added)).IsEqualTo(600);
        await Assert.That(rows[599].Kind).IsEqualTo(LineDiffRowKind.Removed)
            .Because("the removals come before the additions");
    }

    [Test]
    public async Task EmptySides_AreAllAdditionsOrAllRemovals()
    {
        var fromEmpty = LineDiff.Compute(string.Empty, "a\nb");
        var toEmpty = LineDiff.Compute("a\nb", string.Empty);

        await Assert.That(fromEmpty.Count(r => r.Kind == LineDiffRowKind.Added)).IsEqualTo(2)
            .Because("an empty old text is a pure insertion");
        await Assert.That(fromEmpty.Count(r => r.Kind == LineDiffRowKind.Removed)).IsEqualTo(1)
            .Because("splitting an empty text yields one empty line, and that is the line that went away");
        await Assert.That(toEmpty.Count(r => r.Kind == LineDiffRowKind.Removed)).IsEqualTo(2)
            .Because("an empty new text is a pure removal");
        await Assert.That(toEmpty.Count(r => r.Kind == LineDiffRowKind.Added)).IsEqualTo(1)
            .Because("the empty line the new text splits into");
    }

    [Test]
    public async Task NullText_IsAnEmptyText()
    {
        await Assert.That(LineDiff.Compute(null, null).Count).IsEqualTo(1)
            .Because("an empty text is one empty line, matching what Split produces");
    }

    [Test]
    public async Task CarriageReturnsAreNormalisedBeforeMatching()
    {
        // CRLF vs LF is not a change to every line.
        var rows = LineDiff.Compute("a\r\nb\r\nc", "a\nb\nc");

        await Assert.That(rows.All(r => r.Kind == LineDiffRowKind.Unchanged)).IsTrue();
    }

    // ---------------------------------------------------------------------
    // ComputeSideBySide — the aligned projection a two-column view binds.
    // ---------------------------------------------------------------------

    [Test]
    public async Task SideBySide_AReplacementIsOneRowCarryingBothSides()
    {
        var rows = LineDiff.ComputeSideBySide("alpha\nbravo\ncharlie", "alpha\nBRAVO\ncharlie");

        await Assert.That(rows.Count).IsEqualTo(3);
        var modified = rows[1];
        await Assert.That(modified.Kind).IsEqualTo(SideBySideRowKind.Modified);
        await Assert.That(modified.OldText).IsEqualTo("bravo");
        await Assert.That(modified.NewText).IsEqualTo("BRAVO");
    }

    [Test]
    public async Task SideBySide_APureInsertionHasNoLeftSide()
    {
        var rows = LineDiff.ComputeSideBySide("alpha\nbravo", "alpha\nbravo\ncharlie");

        var added = rows.Where(r => r.Kind == SideBySideRowKind.Added).ToList();
        await Assert.That(added.Count).IsEqualTo(1);
        await Assert.That(added[0].OldText).IsNull();
        await Assert.That(added[0].NewText).IsEqualTo("charlie");
    }

    [Test]
    public async Task SideBySide_AnUnbalancedRewrite_KeepsTheLeftovers()
    {
        // One line replaced by three: one paired row and two unpaired adds.
        var rows = LineDiff.ComputeSideBySide("a\nb\nc", "a\nx\ny\nz\nc");

        await Assert.That(rows.Count(r => r.Kind == SideBySideRowKind.Modified)).IsEqualTo(1);
        await Assert.That(rows.Count(r => r.Kind == SideBySideRowKind.Added)).IsEqualTo(2);
        await Assert.That(rows.Count(r => r.Kind == SideBySideRowKind.Unchanged)).IsEqualTo(2);
    }

    [Test]
    public async Task SideBySide_ExactlyOneSideIsNullPerRow()
    {
        var rows = LineDiff.ComputeSideBySide("a\nb\nc\nd", "a\nB2\nc\nd4\ne");

        foreach (var row in rows)
        {
            bool leftNull = row.OldText is null;
            bool rightNull = row.NewText is null;
            await Assert.That(leftNull && !rightNull)
                .IsEqualTo(row.Kind == SideBySideRowKind.Added)
                .Because($"an {row.Kind} row has exactly one side");
        }
    }

    // ---------------------------------------------------------------------
    // ToUnifiedText / TryParseContextBlock — the writer and its reader.
    // ---------------------------------------------------------------------

    [Test]
    public async Task WhatTheWriterEmits_IsWhatTheReaderReadsBack()
    {
        string oldText = "alpha\nbravo\ncharlie\ndelta";
        string newText = "alpha\nbravo\nINSERTED\ncharlie\ndelta\nepsilon";

        var written = LineDiff.ToUnifiedText(LineDiff.Compute(oldText, newText));
        var read = LineDiff.TryParseContextBlock(written, out var rows);

        await Assert.That(read).IsTrue();
        await Assert.That(rows).IsEquivalentTo(LineDiff.Compute(oldText, newText));
    }

    [Test]
    public async Task RemovalBlocksAlsoRoundTrip()
    {
        var written = LineDiff.ToUnifiedText(LineDiff.Compute("a\nb\nc", "a\nc"));
        LineDiff.TryParseContextBlock(written, out var rows);

        await Assert.That(rows.Count).IsEqualTo(3);
        await Assert.That(rows[1].Kind).IsEqualTo(LineDiffRowKind.Removed);
        await Assert.That(rows[1].Text).IsEqualTo("b");
    }

    [Test]
    public async Task ABlockEmbeddedInProse_IsFoundAndTheProseIsNotParsed()
    {
        // The shape EditTool.GenerateContextDiff emits: a summary line, a blank,
        // a caption, then the rows.
        const string output = "Edited src/app.cs: 1 replacement(s) in 1 edit step(s)\n"
                              + "\n"
                              + "Diff (context):\n"
                              + "  alpha\n"
                              + "- bravo\n"
                              + "+ BRAVO\n"
                              + "  charlie\n"
                              + "\n"
                              + "diagnostics: none";

        var found = LineDiff.TryParseContextBlock(output, out var rows);

        await Assert.That(found).IsTrue();
        await Assert.That(rows.Count).IsEqualTo(4);
        await Assert.That(rows[1].Kind).IsEqualTo(LineDiffRowKind.Removed);
        await Assert.That(rows[2].Kind).IsEqualTo(LineDiffRowKind.Added);
    }

    [Test]
    public async Task ProseThatMentionsTheMarkers_IsNotABlock()
    {
        // Every character the old extractor searched for is present as a
        // SUBSTRING and none of them is a row: the markers are not at the start
        // of a line, and "different" is not "diff".
        const string prose = "The result is different from the baseline\n"
                             + "--- section separator ---\n"
                             + "+++ and that is fine\n"
                             + "@@ not a hunk header @@\n"
                             + "| a | b |";

        var found = LineDiff.TryParseContextBlock(prose, out var rows);

        await Assert.That(found).IsFalse();
        await Assert.That(rows).IsEmpty();
    }

    [Test]
    public async Task AnIndentedParagraphWithNoChange_IsNotABlock()
    {
        // Context rows alone carry no information about a change, so a run of
        // them is a paragraph, not a diff.
        const string paragraph = "The summary reads:\n"
                                 + "  first point\n"
                                 + "  second point\n"
                                 + "  third point\n"
                                 + "That is all.";

        await Assert.That(LineDiff.TryParseContextBlock(paragraph, out _)).IsFalse();
    }

    [Test]
    public async Task ANullOrEmptyText_IsNotABlock()
    {
        await Assert.That(LineDiff.TryParseContextBlock(null, out var rows)).IsFalse();
        await Assert.That(LineDiff.TryParseContextBlock(string.Empty, out _)).IsFalse();
        await Assert.That(rows).IsEmpty();
    }

    // ---------------------------------------------------------------------
    // UnifiedDiffParser.TryReadFilePath — the path is a field, not a guess.
    // ---------------------------------------------------------------------

    [Test]
    public async Task FilePath_ComesFromTheDiffHeaderWithThePrefixStripped()
    {
        const string unified = "--- a/src/app.cs\n"
                               + "+++ b/src/app.cs\n"
                               + "@@ -1,2 +1,3 @@\n"
                               + " using System;\n"
                               + "+using System.Text;\n"
                               + " public sealed class App;";

        await Assert.That(UnifiedDiffParser.TryReadFilePath(unified)).IsEqualTo("src/app.cs");
    }

    [Test]
    public async Task FilePath_KeepsAPathThatCarriesNoDiffPrefix()
    {
        await Assert.That(UnifiedDiffParser.TryReadFilePath("--- app.cs\n+++ app.cs\n@@ -1 +1 @@\n-a\n+b"))
            .IsEqualTo("app.cs");
    }

    [Test]
    public async Task FilePath_FallsBackToTheOldSideHeader()
    {
        await Assert.That(UnifiedDiffParser.TryReadFilePath("--- a/src/app.cs\n@@ -1 +1 @@\n-a\n+b"))
            .IsEqualTo("src/app.cs");
    }

    [Test]
    public async Task FilePath_IsNullWhenTheTextIsNotAUnifiedDiff()
    {
        // The old extractor answered "the entire rest of the tool output".
        await Assert.That(UnifiedDiffParser.TryReadFilePath(null)).IsNull();
        await Assert.That(UnifiedDiffParser.TryReadFilePath("Edited src/app.cs: 1 replacement(s)")).IsNull();
        await Assert.That(UnifiedDiffParser.TryReadFilePath("just some prose about --- and +++")).IsNull();
    }
}
