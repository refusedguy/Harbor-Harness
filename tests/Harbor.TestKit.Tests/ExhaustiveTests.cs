// ExhaustiveTests.cs — the controls that say the totality check is not a
// no-op.
//
// WHY THESE EXIST
// ----------------
// A check that never fires is indistinguishable from a check that is broken,
// and a broken check is worse than none because it is believed: every guard
// built on it would be green while the table it guards stayed short. So the
// controls below drive the SAME code the real guards drive, over planted
// inputs:
//
//   1. a table that misses a member must FAIL, and name the member;
//   2. a table that names every member must PASS;
//   3. an empty table must FAIL, naming every member — a table with no rows
//      reads as "nothing is covered", never as "everything is";
//   4. a member that is not a declared member of the enum must not be
//      reported — the check reports what the table FORGOT, not what it
//      invented.
//
// Control 1 is the non-vacuity control the issue asks for: a member of the
// enum that the table does not name has to break the check, or the table
// conversion loses the guarantee the one-test-per-member tests gave.
//
// The planted tables are plain arrays, exactly the shape the real tables
// use — they are not tests themselves, so the controls cost five cases and
// not eight.

using TUnit.Assertions;

namespace Harbor.TestKit.Tests;

/// <summary>
///     A synthetic closed enum for the totality checks. Two members is
///     enough: a table that names one of them is a table with a hole.
/// </summary>
public enum SyntheticClosedEnum
{
    /// <summary>The member the incomplete table names.</summary>
    First,

    /// <summary>The member the incomplete table forgets.</summary>
    Second,
}

/// <summary>
///     The totality check's own tests: the controls that say it is not a
///     no-op.
/// </summary>
public class ExhaustiveTests
{
    /// <summary>
    ///     A table over <see cref="SyntheticClosedEnum" /> that names ONE of
    ///     its TWO members. It is the fixture control 1 needs: the check has
    ///     to notice the member the table forgot.
    /// </summary>
    private static readonly SyntheticClosedEnum[] TableThatMissesAMember = [SyntheticClosedEnum.First];

    /// <summary>
    ///     A table over <see cref="SyntheticClosedEnum" /> that names BOTH of
    ///     its members. It is the fixture control 2 needs: the check has to
    ///     be able to pass.
    /// </summary>
    private static readonly SyntheticClosedEnum[] TableThatNamesEveryMember =
        [SyntheticClosedEnum.First, SyntheticClosedEnum.Second];

    /// <summary>
    ///     Control 1 — the non-vacuity control. A member the table does not
    ///     name must FAIL the check, and the failure must name the member:
    ///     otherwise every guard built on the check is a guard that cannot
    ///     fail.
    /// </summary>
    [Test]
    public async Task TheCheck_Fails_And_Names_The_Member_The_Table_Missed()
    {
        ExhaustivenessException ex = Assert.Throws<ExhaustivenessException>(
            () => Exhaustive.AssertAllCases<SyntheticClosedEnum>(
                TableThatMissesAMember.ToHashSet(),
                "the synthetic table must name every member of the enum"));

        await Assert.That(ex.Message).Contains("Second")
            .Because("the failure has to name the member the table forgot, not merely count it");
        await Assert.That(ex.Message).Contains("SyntheticClosedEnum")
            .Because("the failure has to name the enum the missing member belongs to");
        await Assert.That(ex.Message).DoesNotContain("First")
            .Because("the table DOES name First, so a failure that named it would mean the check "
                + "read something other than the table");
    }

    /// <summary>
    ///     Control 2 — the converse. A table that names every member passes.
    ///     A check that failed everything would satisfy control 1 and be
    ///     useless.
    /// </summary>
    [Test]
    public async Task TheCheck_Passes_When_Every_Member_Is_Named()
    {
        // The call throws when a member is missing; reaching the assertion is
        // the check passing.
        Exhaustive.AssertAllCases<SyntheticClosedEnum>(
            TableThatNamesEveryMember.ToHashSet(),
            "every member is named");

        await Assert.That(TableThatNamesEveryMember.Length).IsEqualTo(2)
            .Because("the synthetic enum has two members and the table names both, so the call above "
                + "had a complete table to pass on rather than an empty subject");
    }

    /// <summary>
    ///     Control 3 — an empty table names no member, so the check must
    ///     report every member as missing. A table that carries no rows at
    ///     all reads as "nothing is covered", never as "everything is":
    ///     that is the difference between a loud failure and a silent one.
    /// </summary>
    [Test]
    public async Task An_Empty_Table_Reports_Every_Member_As_Missing()
    {
        ExhaustivenessException ex = Assert.Throws<ExhaustivenessException>(
            () => Exhaustive.AssertAllCases<SyntheticClosedEnum>(
                Array.Empty<SyntheticClosedEnum>().ToHashSet(),
                "no member is named"));

        await Assert.That(ex.Message).Contains("First");
        await Assert.That(ex.Message).Contains("Second");
    }

    /// <summary>
    ///     Control 4 — the check reports what the table FORGOT, not what it
    ///     invented. A covered set that names a value the enum does not
    ///     declare is not a hole, and saying it is would send a developer to
    ///     fix a table that is already right.
    /// </summary>
    [Test]
    public async Task TheCheck_Reports_Only_The_Members_The_Table_Forgot()
    {
        ExhaustivenessException ex = Assert.Throws<ExhaustivenessException>(
            () => Exhaustive.AssertAllCases<SyntheticClosedEnum>(
                new HashSet<SyntheticClosedEnum> { SyntheticClosedEnum.First, (SyntheticClosedEnum)42 },
                "the synthetic table must name every member of the enum"));

        await Assert.That(ex.Message).Contains("Second")
            .Because("the member the table forgot is the one to report");
        await Assert.That(ex.Message).DoesNotContain("42")
            .Because("42 is not a declared member, so it is not a hole in the table");
    }
}
