using Harbor.Desktop.ViewModels;
using Harbor.Ui.Framework.Rendering.Widgets;
using Harbor.Ui.Framework.State;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;

namespace Harbor.Ui.Framework.Tests;

/// <summary>
///     Issue #1236 — DiffPlex pilot, strictly outside the CellForge engine.
///     The pilot returns the existing <see cref="LineDiff"/> row shapes from the
///     DiffPlex engine, opt-in only: with the flag unset every preview renders
///     exactly what it rendered before the pilot existed.
/// </summary>
public class DiffPlexPilotTests
{
    [Test]
    public async Task Pilot_MidFileInsertion_IsOneAdditionAndNothingElse()
    {
        bool ok = DiffPlexPilot.TryComputeRows(
            "alpha\nbravo\ncharlie",
            "alpha\nbravo\nINSERTED\ncharlie",
            out IReadOnlyList<LineDiffRow> rows);

        await Assert.That(ok).IsTrue();
        await Assert.That(rows.Count(r => r.Kind == LineDiffRowKind.Added)).IsEqualTo(1);
        await Assert.That(rows.Count(r => r.Kind == LineDiffRowKind.Removed)).IsEqualTo(0);
        await Assert.That(rows.Count(r => r.Kind == LineDiffRowKind.Unchanged)).IsEqualTo(3);
        await Assert.That(rows[2].Text).IsEqualTo("INSERTED");
        await Assert.That(rows[2].NewLineNumber).IsEqualTo(3);
    }

    [Test]
    public async Task Pilot_Rows_AccountEveryInputLineExactlyOnce()
    {
        const string before = "one\ntwo\nthree";
        const string after = "one\nTWO\nthree\nfour";

        bool ok = DiffPlexPilot.TryComputeRows(before, after, out IReadOnlyList<LineDiffRow> rows);

        await Assert.That(ok).IsTrue();
        int oldCovered = rows.Count(r => r.Kind is LineDiffRowKind.Unchanged or LineDiffRowKind.Removed);
        int newCovered = rows.Count(r => r.Kind is LineDiffRowKind.Unchanged or LineDiffRowKind.Added);
        await Assert.That(oldCovered).IsEqualTo(3);
        await Assert.That(newCovered).IsEqualTo(4);
    }

    [Test]
    public async Task Pilot_IdenticalTexts_AreAllUnchanged()
    {
        bool ok = DiffPlexPilot.TryComputeRows("same\nlines", "same\nlines", out IReadOnlyList<LineDiffRow> rows);

        await Assert.That(ok).IsTrue();
        await Assert.That(rows.Count).IsEqualTo(2);
        await Assert.That(rows.All(r => r.Kind == LineDiffRowKind.Unchanged)).IsTrue();
    }

    [Test]
    public async Task Pilot_NullInputs_ReadAsEmpty()
    {
        bool ok = DiffPlexPilot.TryComputeRows(null, null, out IReadOnlyList<LineDiffRow> rows);

        await Assert.That(ok).IsTrue();
        await Assert.That(rows.All(r => r.Kind == LineDiffRowKind.Unchanged)).IsTrue();
    }

    [Test]
    public async Task Pilot_OversizedInput_DegradesToFallback()
    {
        string huge = new string('x', DiffPlexPilot.MaxPilotInputChars + 1);

        bool rows = DiffPlexPilot.TryComputeRows(huge, string.Empty, out _);
        bool unified = DiffPlexPilot.TryComputeUnified(huge, string.Empty, out string? text);
        bool preview = DiffPlexPilot.TryComputePreview(huge, string.Empty, 6, out string? cut);

        await Assert.That(rows).IsFalse();
        await Assert.That(unified).IsFalse();
        await Assert.That(text is null).IsTrue();
        await Assert.That(preview).IsFalse();
        await Assert.That(cut is null).IsTrue();
    }

    [Test]
    public async Task Pilot_Preview_TruncatesWithTheSharedMarker()
    {
        bool ok = DiffPlexPilot.TryComputePreview("a\nb\nc\nd", "a\nB\nc\nd", 1, out string? preview);

        await Assert.That(ok).IsTrue();
        await Assert.That(preview is not null).IsTrue();
        await Assert.That(preview!.Contains(DiffPreview.DiffTruncatedSentinel)).IsTrue();
    }

    [Test]
    public async Task Pilot_Preview_FitsWithoutMarker()
    {
        bool ok = DiffPlexPilot.TryComputePreview("a", "b", DiffPreview.MaxPreviewLines, out string? preview);

        await Assert.That(ok).IsTrue();
        await Assert.That(preview is not null).IsTrue();
        await Assert.That(preview!.Contains(DiffPreview.DiffTruncatedSentinel)).IsFalse();
    }

    [Test]
    public async Task Pilot_Preview_RejectsNonPositiveBudget()
    {
        bool ok = DiffPlexPilot.TryComputePreview("a", "b", 0, out string? preview);

        await Assert.That(ok).IsFalse();
        await Assert.That(preview is null).IsTrue();
    }

    [Test]
    public async Task Pilot_WordBonus_AnchorsOnSharedWords()
    {
        bool ok = DiffPlexPilot.TrySegmentWords("Hello brave world", "Hello bold world", out WordDiffSides sides);

        await Assert.That(ok).IsTrue();
        await Assert.That(sides.Removed.Any(s => s.Kind == WordSegKind.Deleted && s.Text.Contains("brave"))).IsTrue();
        await Assert.That(sides.Inserted.Any(s => s.Kind == WordSegKind.Added && s.Text.Contains("bold"))).IsTrue();
        await Assert.That(sides.Removed.Any(s => s.Kind == WordSegKind.Equal && s.Text.Contains("Hello"))).IsTrue();
        await Assert.That(sides.Inserted.Any(s => s.Kind == WordSegKind.Equal && s.Text.Contains("world"))).IsTrue();
    }

    [Test]
    public async Task Pilot_WordBonus_EmptyLines_HaveEmptySides()
    {
        bool ok = DiffPlexPilot.TrySegmentWords(string.Empty, string.Empty, out WordDiffSides sides);

        await Assert.That(ok).IsTrue();
        await Assert.That(sides.Removed.Count).IsEqualTo(0);
        await Assert.That(sides.Inserted.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Pilot_WordBonus_OversizedLine_DegradesToFallback()
    {
        string huge = new string('x', WordDiff.MaxPairableLineChars + 1);

        bool ok = DiffPlexPilot.TrySegmentWords(huge, "tiny", out _);

        await Assert.That(ok).IsFalse();
    }

    [Test]
    public async Task Pilot_Flag_MatchesEnvironment()
    {
        string? flag = Environment.GetEnvironmentVariable(DiffPlexPilot.EnableEnvVar);
        bool expected = string.Equals(flag, "1", StringComparison.OrdinalIgnoreCase)
            || string.Equals(flag, "true", StringComparison.OrdinalIgnoreCase);

        await Assert.That(DiffPlexPilot.IsEnabled).IsEqualTo(expected);
    }

    [Test]
    public async Task Pilot_DesktopDefaultPath_UnchangedWhileFlagUnset()
    {
        // The pilot branch only runs with the flag set; CI never sets it, so
        // this pins the shipped rendering. Under an explicit flag the pilot
        // owns the text instead (covered by the row tests above).
        if (DiffPlexPilot.IsEnabled)
        {
            return;
        }

        var vm = new DiffViewModel();
        vm.ComputeDiff();

        await Assert.That(vm.DiffText.Contains("Harbor")).IsTrue();
    }
}
