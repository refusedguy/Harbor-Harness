using System.Text;
using Harbor.App.Cli.Commands;

namespace Harbor.App.Cli.Tests;

/// <summary>
///     #2686 J04. The review guide is a pure projection of a unified diff.
///     These tests pin what a reviewer sees (file, line span, reason, risk flag),
///     not how the diff was fetched or how the JSON was written.
/// </summary>
public class PrReviewGuideTests
{
    [Test]
    public async Task Build_MultipleHunks_YieldsOneStopPerHunkWithPathReason()
    {
        const string diff = """
            diff --git a/src/Plain.cs b/src/Plain.cs
            --- a/src/Plain.cs
            +++ b/src/Plain.cs
            @@ -1,3 +1,4 @@
            +var added = true;
            diff --git a/src/Permissions/Rules.cs b/src/Permissions/Rules.cs
            --- a/src/Permissions/Rules.cs
            +++ b/src/Permissions/Rules.cs
            @@ -10 +10,2 @@
            +new("tool", "*", Allow),
            """;

        IReadOnlyList<PrGuideEntry> entries = PrReviewGuide.Build(diff);

        await Assert.That(entries.Count).IsEqualTo(2);
        await Assert.That(entries[0].File).IsEqualTo("src/Plain.cs");
        await Assert.That(entries[0].Lines).IsEqualTo("1-4");
        await Assert.That(entries[0].Why).Contains("behavior change");
        await Assert.That(entries[0].LookClosely).IsFalse();
        await Assert.That(entries[1].File).IsEqualTo("src/Permissions/Rules.cs");
        await Assert.That(entries[1].Lines).IsEqualTo("10-11");
        await Assert.That(entries[1].Why).Contains("permission boundary");
        await Assert.That(entries[1].LookClosely).IsTrue();
    }

    [Test]
    public async Task Build_DeletedHunk_MarksLookCloselyUsingTheOldRange()
    {
        const string diff = """
            diff --git a/src/Old.cs b/src/Old.cs
            --- a/src/Old.cs
            +++ /dev/null
            @@ -5,4 +0,0 @@
            -old code
            """;

        IReadOnlyList<PrGuideEntry> entries = PrReviewGuide.Build(diff);

        await Assert.That(entries.Count).IsEqualTo(1);
        await Assert.That(entries[0].Lines).IsEqualTo("5-8");
        await Assert.That(entries[0].LookClosely).IsTrue();
    }

    [Test]
    public async Task Build_SingleLineHunk_UsesBareLineNumber()
    {
        const string diff = """
            diff --git a/docs/GUIDE.md b/docs/GUIDE.md
            --- a/docs/GUIDE.md
            +++ b/docs/GUIDE.md
            @@ -7 +7 @@
            +new text
            """;

        PrGuideEntry entry = PrReviewGuide.Build(diff).Single();

        await Assert.That(entry.Lines).IsEqualTo("7");
        await Assert.That(entry.Why).Contains("documentation");
    }

    [Test]
    public async Task Build_EmptyOrHunklessDiff_ReturnsEmptyGuide()
    {
        await Assert.That(PrReviewGuide.Build(null)).IsEmpty();
        await Assert.That(PrReviewGuide.Build("   ")).IsEmpty();
        await Assert.That(PrReviewGuide.Build("diff --git a/a b/a\n--- a/a\n+++ b/a\n")).IsEmpty();
    }

    [Test]
    public async Task Build_LargeDiff_IsCappedSoItCannotFloodStdout()
    {
        var diff = new StringBuilder();
        for (int i = 0; i < PrReviewGuide.MaxEntries + 10; i++)
        {
            diff.Append("diff --git a/f").Append(i).Append(" b/f").Append(i).Append('\n');
            diff.Append("--- a/f").Append(i).Append('\n');
            diff.Append("+++ b/f").Append(i).Append('\n');
            diff.Append("@@ -1 +1 @@\n");
            diff.Append("+x\n");
        }

        await Assert.That(PrReviewGuide.Build(diff.ToString()).Count).IsEqualTo(PrReviewGuide.MaxEntries);
    }
}
