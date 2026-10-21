namespace Harbor.TestKit.Tests;

/// <summary>
///     The golden helper's mechanics: where a baseline lives, how it is
///     compared, and what a missing one says. The helper is shared by six
///     lanes, so a defect in it is a defect in every one of them.
/// </summary>
/// <remarks>
///     NotInParallel (bare): the helper reads a process-wide flag, and the
///     sibling <c>GoldenUpdateModeTests</c> writes it. The bare form is the
///     one that keeps this class off the flag while it is set.
/// </remarks>
[NotInParallel]
public class GoldenTests
{
    [Test]
    public async Task Normalize_Turns_Crlf_And_Lone_Cr_Into_Lf()
    {
        await Assert.That(Golden.Normalize("a\r\nb\rc\nd")).IsEqualTo("a\nb\nc\nd")
            .Because(
                "a baseline written on a Windows checkout has to compare equal to one written on a Linux checkout — "
                + "otherwise the same code fails on one host and passes on another");
    }

    [Test]
    public async Task Normalize_Leaves_Lf_Alone()
    {
        await Assert.That(Golden.Normalize("a\nb")).IsEqualTo("a\nb")
            .Because("the normalisation is a line-ending contract, not a reformatting contract");
    }

    [Test]
    public async Task FixtureDir_Lives_Under_Tests_Fixtures()
    {
        string dir = Golden.FixtureDir("celldiff");

        await Assert.That(dir).IsEqualTo(Path.Combine(Golden.Root, "tests", "fixtures", "celldiff"))
            .Because("every suite's baselines live in one place, and the place is the repository's fixture tree");
    }

    [Test]
    public async Task BaselinePath_Names_The_Baseline_File()
    {
        string dir = Golden.FixtureDir("celldiff");

        await Assert.That(Golden.BaselinePath(dir, "ce3-diff-block"))
            .IsEqualTo(Path.Combine(dir, "ce3-diff-block.golden.txt"))
            .Because("the extension is part of the contract: a file without it is not a baseline");
    }

    [Test]
    [Arguments("committed\n", "committed\n")]
    [Arguments("committed\r\n", "committed\n")]
    public async Task VerifyText_Returns_The_Committed_Baseline(string committed, string expected)
    {
        using var scope = new TempDirScope("golden");
        File.WriteAllText(Path.Combine(scope.Path, "sample.golden.txt"), committed);

        string returned = Golden.VerifyText(scope.Path, "sample", "produced\n");

        await Assert.That(returned).IsEqualTo(expected)
            .Because(
                "the caller asserts against the committed text; a helper that returned the actual would make every "
                + "golden assertion vacuous — and the committed text is normalised, because a checkout that "
                + "materialised the baseline with CRLF must still compare equal to the LF text the code produced");
    }

    [Test]
    public async Task VerifyText_Fails_With_The_Seeding_Command_When_The_Baseline_Is_Missing()
    {
        using var scope = new TempDirScope("golden");

        var ex = Assert.Throws<InvalidOperationException>(
            () => Golden.VerifyText(scope.Path, "missing", "produced\n"));

        await Assert.That(ex.Message.Contains(Golden.UpdateEnvironmentVariable, StringComparison.Ordinal)).IsTrue()
            .Because("a missing baseline is only actionable if the message says how to create it");
        await Assert.That(ex.Message.Contains("missing", StringComparison.Ordinal)).IsTrue()
            .Because("the message has to say what is missing, not just that something is");
    }

    [Test]
    public async Task DescribeDifference_Names_The_First_Differing_Line()
    {
        string report = Golden.DescribeDifference("a\nb\nc\n", "a\nX\nc\n");

        await Assert.That(report.Contains("line 2", StringComparison.Ordinal)).IsTrue()
            .Because("a report that says only 'they differ' makes the reader diff two files by hand");
        await Assert.That(report.Contains("baseline: b", StringComparison.Ordinal)).IsTrue();
        await Assert.That(report.Contains("actual:  X", StringComparison.Ordinal)).IsTrue();
    }
}
