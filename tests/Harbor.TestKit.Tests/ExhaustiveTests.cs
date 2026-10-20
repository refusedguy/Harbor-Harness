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
//   2. a set that names every member must PASS;
//   3. an empty set must FAIL, naming every member — an empty table reads
//      as "nothing is covered", never as "everything is";
//   4. a table that cannot be read must FAIL loudly rather than pass;
//   5. a complete table must PASS.
//
// Control 1 is the non-vacuity control the issue asks for: a member of the
// enum that the table does not name has to break the check, or the table
// conversion loses the guarantee the one-test-per-member tests gave.
//
// The fixture tables below are REAL tests, and their rows are real cases —
// the cost of the controls is three extra cases in this project's report,
// which is the price of proving the check can fail and can pass.

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
    [Test]
    [Arguments(SyntheticClosedEnum.First, "first")]
    public async Task ATable_That_Misses_A_Member(SyntheticClosedEnum member, string label)
    {
        await Assert.That(label).IsEqualTo(member.ToString().ToLowerInvariant());
    }

    /// <summary>
    ///     A table over <see cref="SyntheticClosedEnum" /> that names BOTH of
    ///     its members. It is the fixture control 5 needs: the check has to
    ///     be able to pass.
    /// </summary>
    [Test]
    [Arguments(SyntheticClosedEnum.First, "first")]
    [Arguments(SyntheticClosedEnum.Second, "second")]
    public async Task ATable_That_Names_Every_Member(SyntheticClosedEnum member, string label)
    {
        await Assert.That(label).IsEqualTo(member.ToString().ToLowerInvariant());
    }

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
            () => Exhaustive.AssertAllCases<SyntheticClosedEnum>([
                (typeof(ExhaustiveTests), nameof(ATable_That_Misses_A_Member)),
            ], "the synthetic table must name every member of the enum"));

        await Assert.That(ex.Message).Contains("Second")
            .Because("the failure has to name the member the table forgot, not merely count it");
        await Assert.That(ex.Message).Contains("SyntheticClosedEnum")
            .Because("the failure has to name the enum the missing member belongs to");
    }

    /// <summary>
    ///     Control 2 — the converse. A set that names every member passes. A
    ///     check that failed everything would satisfy control 1 and be
    ///     useless.
    /// </summary>
    [Test]
    public async Task TheCheck_Passes_When_Every_Member_Is_Named()
    {
        var covered = new HashSet<SyntheticClosedEnum>(Enum.GetValues<SyntheticClosedEnum>());

        // The call throws when a member is missing; reaching the assertion is
        // the check passing.
        Exhaustive.AssertAllCases<SyntheticClosedEnum>(covered, "every member is named");

        await Assert.That(covered.Count).IsEqualTo(2)
            .Because("the synthetic enum has two members and the set names both, so the call above "
                + "had a complete set to pass on rather than an empty subject");
    }

    /// <summary>
    ///     Control 3 — an empty set names no member, so the check must
    ///     report every member as missing. A table that carries no rows at
    ///     all reads as "nothing is covered", never as "everything is":
    ///     that is the difference between a loud failure and a silent one.
    /// </summary>
    [Test]
    public async Task An_Empty_Set_Reports_Every_Member_As_Missing()
    {
        ExhaustivenessException ex = Assert.Throws<ExhaustivenessException>(
            () => Exhaustive.AssertAllCases<SyntheticClosedEnum>(
                new HashSet<SyntheticClosedEnum>(), "no member is named"));

        await Assert.That(ex.Message).Contains("First");
        await Assert.That(ex.Message).Contains("Second");
    }

    /// <summary>
    ///     Control 4 — a table that cannot be read must FAIL loudly. A
    ///     method that does not exist is not a table with no rows; it is a
    ///     wiring mistake, and the two must not look alike.
    /// </summary>
    [Test]
    public async Task TheCheck_Refuses_A_Table_It_Cannot_Read()
    {
        ExhaustivenessException ex = Assert.Throws<ExhaustivenessException>(
            () => Exhaustive.AssertAllCases<SyntheticClosedEnum>([
                (typeof(ExhaustiveTests), "NoSuchTable"),
            ], "the table has to exist"));

        await Assert.That(ex.Message).Contains("NoSuchTable")
            .Because("the failure has to say which table could not be read");
    }

    /// <summary>
    ///     Control 5 — the table path can pass. Without this, control 1
    ///     would also be satisfied by a check that fails every table.
    /// </summary>
    [Test]
    public async Task TheCheck_Passes_A_Table_That_Names_Every_Member()
    {
        await Assert.That(Enum.GetValues<SyntheticClosedEnum>().Length).IsEqualTo(2).Because("the synthetic enum has two members");
        Exhaustive.AssertAllCases<SyntheticClosedEnum>([
            (typeof(ExhaustiveTests), nameof(ATable_That_Names_Every_Member)),
        ], "the synthetic table names every member of the enum");
    }
}
