// ApprovalGateWaiterRaceParityTests.cs — the proof that the allocation
// pre-check in the approval-gate waiter rule did not move a verdict.
//
// WHAT THE PRE-CHECK IS
// ---------------------
// `ApprovalGateWaiterRaceRules.FindRacesInStrippedCode` now answers "this file
// cannot hold a race" BEFORE it copies the file, scans its method headers and
// runs two regular expressions over every method body. The check is: does the
// text contain `WaitForDecisionAsync`? Both waiter patterns — the parked-local
// one and the anywhere one — require that literal, so a file without it
// cannot produce a race.
//
// WHY A PARITY PROOF, AND NOT "THE TESTS ARE STILL GREEN"
// -------------------------------------------------------
// A rule that finds nothing reports nothing, which is green. So a pre-check
// that was wrong in the direction of "finds less" would leave every existing
// test of this rule green, and the defect it was written for alive. The proof
// is therefore over the LIVE TREE and against the UNSKIPPED scan, which is
// kept in the rule file as `FindRacesInStrippedCodeUnskipped` for exactly
// this purpose: file by file, the two must report the same races.
//
// WHAT IS COMPARED
// ----------------
// The whole race record — line, method, waiter and disposition — not a count.
// A count would say "both found three" and not say whether they found the
// same three.

using System.Text;

namespace Harbor.Architecture.Tests;

/// <summary>
///     Proves that the cheap pre-check the approval-gate waiter rule runs before
///     its scan reports exactly what the scan itself reports, on every file of
///     the live tree.
/// </summary>
public sealed class ApprovalGateWaiterRaceParityTests
{
    /// <summary>
    ///     The two scans agree, file by file, over the rule's own scope.
    /// </summary>
    [Test]
    public async Task The_Skipped_Scan_And_The_Unskipped_Scan_Agree_On_Every_File()
    {
        await Assert.That(RepoPaths.RepoRoot).IsNotNull()
            .Because(
                "the proof reads the repository's own source; with no root it would compare nothing "
                + "and pass for the wrong reason.");

        IReadOnlyList<string> files = ApprovalGateWaiterRaceRules.EnumerateTestSources();

        int compared = 0;
        int skipped = 0;
        var mismatches = new List<string>();

        foreach (string file in files)
        {
            SourceTreeFile? source = SharedSourceTreeSnapshot.Shared.Get(file);
            if (source is null)
            {
                continue;
            }

            compared++;
            if (!source.StrippedText.Contains("WaitForDecisionAsync", StringComparison.Ordinal))
            {
                skipped++;
            }

            IReadOnlyList<ApprovalGateWaiterRaceRules.WaiterRace> withCheck =
                ApprovalGateWaiterRaceRules.FindRacesInStrippedCode(source.StrippedText);
            IReadOnlyList<ApprovalGateWaiterRaceRules.WaiterRace> withoutCheck =
                ApprovalGateWaiterRaceRules.FindRacesInStrippedCodeUnskipped(source.StrippedText);

            if (!SameRaces(withCheck, withoutCheck))
            {
                mismatches.Add(
                    $"{SourceScan.Relative(file)} — the pre-checked scan reports "
                    + $"{Describe(withCheck)} and the unskipped scan reports {Describe(withoutCheck)}.");
            }
        }

        Console.WriteLine(
            $"[arch-count] approval-gate parity files={compared} skipped-by-the-pre-check={skipped} "
            + $"mismatches={mismatches.Count}");

        await Assert.That(compared).IsGreaterThan(500)
            .Because(
                "the rule's scope is the whole test tree, which holds more than five hundred files; a "
                + "smaller number means the walk broke and the comparison below proved nothing.");
        await Assert.That(skipped).IsGreaterThan(0)
            .Because(
                "the pre-check is supposed to skip the files that do not mention the call, and if it "
                + "skipped nothing it bought nothing — the proof would be about a scan that does not "
                + "exist.");
        await Assert.That(mismatches).IsEmpty()
            .Because(
                "the pre-check may only skip a file the scan would have reported nothing for: "
                + string.Join(" | ", mismatches));
    }

    /// <summary>
    ///     What the pre-check saves: the two scans, over the rule's own scope,
    ///     measured side by side in the same run.
    /// </summary>
    [Test]
    public async Task Measure_What_The_Pre_Check_Saves()
    {
        await Assert.That(RepoPaths.RepoRoot).IsNotNull()
            .Because(
                "the measurement reads the repository's own source; with no root it would measure "
                + "nothing and report a cost of zero that looks like a free scan.");

        long beforeBytes = 0;
        long afterBytes = 0;
        int files = 0;
        int skipped = 0;

        foreach (string file in ApprovalGateWaiterRaceRules.EnumerateTestSources())
        {
            SourceTreeFile? source = SharedSourceTreeSnapshot.Shared.Get(file);
            if (source is null)
            {
                continue;
            }

            files++;

            long mark = GC.GetAllocatedBytesForCurrentThread();
            _ = ApprovalGateWaiterRaceRules.FindRacesInStrippedCodeUnskipped(source.StrippedText);
            beforeBytes += GC.GetAllocatedBytesForCurrentThread() - mark;

            mark = GC.GetAllocatedBytesForCurrentThread();
            _ = ApprovalGateWaiterRaceRules.FindRacesInStrippedCode(source.StrippedText);
            afterBytes += GC.GetAllocatedBytesForCurrentThread() - mark;

            if (!source.StrippedText.Contains("WaitForDecisionAsync", StringComparison.Ordinal))
            {
                skipped++;
            }
        }

        double ratio = afterBytes == 0 ? 0 : (double)beforeBytes / afterBytes;
        Console.WriteLine(
            $"[arch-cost] fix=approval-gate-pre-check before-bytes={beforeBytes} after-bytes={afterBytes} "
            + $"ratio={ratio:F2} files={files} skipped={skipped}");

        await Assert.That(beforeBytes).IsGreaterThan(0L)
            .Because(
                "the unskipped scan must have allocated something: a zero there means the measurement "
                + "measured nothing and the ratio below is a number about nothing.");
        await Assert.That(afterBytes).IsGreaterThan(0L)
            .Because(
                "the pre-checked scan must have allocated something: a zero there means the measurement "
                + "measured nothing and the ratio below is a number about nothing.");
    }

    /// <summary>Whether two race lists are the same, record by record.</summary>
    /// <param name="left">The first list.</param>
    /// <param name="right">The second list.</param>
    private static bool SameRaces(
        IReadOnlyList<ApprovalGateWaiterRaceRules.WaiterRace> left,
        IReadOnlyList<ApprovalGateWaiterRaceRules.WaiterRace> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        for (int i = 0; i < left.Count; i++)
        {
            if (left[i] != right[i])
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>One race list, in a form a failure message can carry.</summary>
    /// <param name="races">The list to describe.</param>
    private static string Describe(IReadOnlyList<ApprovalGateWaiterRaceRules.WaiterRace> races)
    {
        if (races.Count == 0)
        {
            return "none";
        }

        var text = new StringBuilder();
        foreach (ApprovalGateWaiterRaceRules.WaiterRace race in races)
        {
            if (text.Length > 0)
            {
                text.Append(", ");
            }

            text.Append(race);
        }

        return text.ToString();
    }
}
