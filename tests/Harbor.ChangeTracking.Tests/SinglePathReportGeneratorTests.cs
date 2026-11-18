using Harbor.ChangeTracking;
using Harbor.TestKit;

namespace Harbor.ChangeTracking.Tests;

/// <summary>
///     Minimal slice for #379: the single-path verification-report generator
///     turns one diff line plus recorded checks into a report with the five
///     required sections. Pure logic — no git, no harbor home.
/// </summary>
public class SinglePathReportGeneratorTests
{
    private const string Base = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string Head = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    [Test]
    public async Task ParseDiffLine_Modification_YieldsPathAndStatus()
    {
        var parsed = SinglePathReportGenerator.ParseDiffLine("M\tsrc/Foo.cs");
        await Assert.That(parsed.IsSuccess).IsTrue();
        await Assert.That(parsed.Value.Path).IsEqualTo("src/Foo.cs");
        await Assert.That(parsed.Value.Status).IsEqualTo("M");
    }

    [Test]
    public async Task ParseDiffLine_Rename_UsesDestinationPathAndLeadingStatus()
    {
        var parsed = SinglePathReportGenerator.ParseDiffLine("R100\told/Foo.cs\tnew/Foo.cs");
        await Assert.That(parsed.IsSuccess).IsTrue();
        await Assert.That(parsed.Value.Path).IsEqualTo("new/Foo.cs");
        await Assert.That(parsed.Value.Status).IsEqualTo("R");
    }

    [Test]
    public async Task ParseDiffLine_Malformed_Fails()
    {
        await Assert.That(SinglePathReportGenerator.ParseDiffLine(null).IsFailure).IsTrue();
        await Assert.That(SinglePathReportGenerator.ParseDiffLine("   ").IsFailure).IsTrue();
        await Assert.That(SinglePathReportGenerator.ParseDiffLine("M").IsFailure).IsTrue();
    }

    [Test]
    public async Task Generate_OnePath_RendersFiveSectionsInOrder()
    {
        ChangedPathEntry path = SinglePathReportGenerator.ParseDiffLine("M\tsrc/Foo.cs").Value;
        ChangeReport report = SinglePathReportGenerator.Generate(
            "run-1", Base, Head, path,
            [RecordedCheck.Passed("build", "dotnet build", Head, ["PATH"])],
            0, 600, 50, false);

        string text = report.ToPlainText();
        string[] sections =
        [
            $"== {SinglePathReportGenerator.ChangedSection} (",
            $"== {SinglePathReportGenerator.VerifiedSection} (",
            $"== {SinglePathReportGenerator.NotVerifiedSection} (",
            $"== {SinglePathReportGenerator.LimitsSection} (",
            $"== {SinglePathReportGenerator.RisksSection} ("
        ];
        int prev = -1;
        for (int i = 0; i < sections.Length; i++)
        {
            int at = text.IndexOf(sections[i], StringComparison.Ordinal);
            await Assert.That(at > prev).IsTrue();
            prev = at;
        }
        await Assert.That(text.Contains("src/Foo.cs")).IsTrue();
    }

    [Test]
    public async Task Generate_NoChecks_NotVerifiedHasNoChecksDeclared()
    {
        ChangedPathEntry path = SinglePathReportGenerator.ParseDiffLine("A\tsrc/Bar.cs").Value;
        ChangeReport report = SinglePathReportGenerator.Generate(
            "run-2", Base, Head, path, [], 0, 600, 50, false);

        bool named = false;
        bool external = false;
        for (int i = 0; i < report.NotVerified.Count; i++)
        {
            if (report.NotVerified[i].Reason == "no checks declared")
                named = true;
            if (report.NotVerified[i].What == "external side effects"
                && report.NotVerified[i].Reason == "not observable")
                external = true;
        }
        await Assert.That(named).IsTrue();
        await Assert.That(external).IsTrue();
        await Assert.That(report.ExitCode).IsEqualTo(ChangeReport.ExitAllPassed);
    }

    [Test]
    public async Task Generate_PassedCheck_AppearsUnderVerified()
    {
        ChangedPathEntry path = SinglePathReportGenerator.ParseDiffLine("M\tsrc/Foo.cs").Value;
        ChangeReport report = SinglePathReportGenerator.Generate(
            "run-3", Base, Head, path,
            [RecordedCheck.Passed("tests", "dotnet test", Head, ["CI", "PATH"])],
            0, 600, 50, false);

        await Assert.That(report.Changed.Count).IsEqualTo(1);
        await Assert.That(report.Verified.Count).IsEqualTo(1);
        await Assert.That(report.Verified[0].Name).IsEqualTo("tests");
        await Assert.That(report.Verified[0].Revision).IsEqualTo(Head);
        await Assert.That(report.ExitCode).IsEqualTo(ChangeReport.ExitAllPassed);
    }

    [Test]
    public async Task Generate_Deterministic_ByteIdenticalJson()
    {
        ChangeReport first = SinglePathReportGenerator.Generate(
            "run-4", Base, Head,
            SinglePathReportGenerator.ParseDiffLine("M\tsrc/Foo.cs").Value,
            [RecordedCheck.Passed("b", "b-cmd", Head, ["HOME", "PATH"])],
            1, 600, 50, true);
        ChangeReport second = SinglePathReportGenerator.Generate(
            "run-4", Base, Head,
            SinglePathReportGenerator.ParseDiffLine("M\tsrc/Foo.cs").Value,
            [RecordedCheck.Passed("b", "b-cmd", Head, ["PATH", "HOME"])],
            1, 600, 50, true);

        byte[] a = first.ToJsonBytes();
        byte[] b = second.ToJsonBytes();
        await Assert.That(a.Length).IsEqualTo(b.Length);
        await Assert.That(a.SequenceEqual(b)).IsTrue();
    }
}
