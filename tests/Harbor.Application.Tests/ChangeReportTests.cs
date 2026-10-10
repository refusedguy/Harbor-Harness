using Harbor.Application.Sessions;

namespace Harbor.Application.Tests;

/// <summary>
///     S5 slice 1 (#379): the verification report is derived from recorded
///     artifacts only. Block order, <c>(none)</c> handling, derived
///     <c>NotVerified</c>, <c>KnownRisks</c> triggers, determinism, exit
///     codes, and the closed write path. Pure logic — no git, no harbor
///     home, runs anywhere.
/// </summary>
public class ChangeReportTests
{
    private const string Base = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string Head = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    private static ChangeReport EmptyReport() =>
        ChangeReport.Create("run-1", Base, Head, [], [], 0, 600, 50, false);

    [Test]
    public async Task Blocks_RenderInOrder_EmptyBlocksPrintNone()
    {
        string text = EmptyReport().ToPlainText();
        string[] blocks = ["== Run (", "== Changed (", "== Verified (", "== NotVerified (", "== Limits (", "== KnownRisks ("];
        int prev = -1;
        for (int i = 0; i < blocks.Length; i++)
        {
            int at = text.IndexOf(blocks[i], StringComparison.Ordinal);
            await Assert.That(at > prev).IsTrue();
            prev = at;
        }
        await Assert.That(text.Contains("(none)")).IsTrue();
    }

    [Test]
    public async Task PlainText_Fits80Columns_AndHasNoAnsi()
    {
        ChangeReport report = ChangeReport.Create(
            "run-1", Base, Head,
            [ChangedPathEntry.Create("src/Foo.cs", "M")],
            [RecordedCheck.Passed("build", "dotnet build --no-restore --some-very-long-argument-list-to-force-wrapping one two three four", Head, ["HOME", "PATH"])],
            0, 600, 50, false);
        string text = report.ToPlainText();
        await Assert.That(text.Contains('\x1b')).IsFalse();
        string[] lines = text.Split('\n');
        for (int i = 0; i < lines.Length; i++)
            await Assert.That(lines[i].Length <= ChangeReport.TextWidth).IsTrue();
    }

    [Test]
    public async Task PassedCheck_AppearsUnderVerified_WithFullCitation()
    {
        ChangeReport report = ChangeReport.Create(
            "run-1", Base, Head,
            [ChangedPathEntry.Create("src/Foo.cs", "M")],
            [RecordedCheck.Passed("build", "dotnet build", Head, ["PATH", "HOME"])],
            0, 600, 50, false);
        await Assert.That(report.Verified.Count).IsEqualTo(1);
        await Assert.That(report.Verified[0].Name).IsEqualTo("build");
        await Assert.That(report.Verified[0].Command).IsEqualTo("dotnet build");
        await Assert.That(report.Verified[0].ExitCode).IsEqualTo(0);
        await Assert.That(report.Verified[0].Revision).IsEqualTo(Head);
        await Assert.That(report.ExitCode).IsEqualTo(ChangeReport.ExitAllPassed);
        string json = System.Text.Encoding.UTF8.GetString(report.ToJsonBytes());
        await Assert.That(json.Contains("\"build\"")).IsTrue();
        await Assert.That(json.Contains(Head)).IsTrue();
    }

    [Test]
    public async Task FailedAndTimedOutChecks_LandInNotVerified_ExitIsOne()
    {
        ChangeReport report = ChangeReport.Create(
            "run-1", Base, Head,
            [ChangedPathEntry.Create("src/Foo.cs", "M")],
            [RecordedCheck.Failed("tests", "dotnet test", 1, Head, ["PATH"]),
             RecordedCheck.DidNotRun("lint", "dotnet lint", "killed by timeout", Head, ["PATH"], timedOut: true)],
            0, 600, 50, false);
        await Assert.That(report.Verified.Count).IsEqualTo(0);
        await Assert.That(report.ExitCode).IsEqualTo(ChangeReport.ExitCheckFailed);
        bool hasTests = false;
        bool hasLint = false;
        for (int i = 0; i < report.NotVerified.Count; i++)
        {
            if (report.NotVerified[i].What == "tests")
                hasTests = true;
            if (report.NotVerified[i].What == "lint")
                hasLint = true;
        }
        await Assert.That(hasTests).IsTrue();
        await Assert.That(hasLint).IsTrue();
    }

    [Test]
    public async Task CheckWithoutRevision_NeverAppearsUnderVerified()
    {
        ChangeReport report = ChangeReport.Create(
            "run-1", Base, Head,
            [ChangedPathEntry.Create("src/Foo.cs", "M")],
            [RecordedCheck.DidNotRun("build", "dotnet build", "no revision recorded", null, ["PATH"])],
            0, 600, 50, false);
        await Assert.That(report.Verified.Count).IsEqualTo(0);
        await Assert.That(report.NotVerified.Count >= 2).IsTrue();
    }

    [Test]
    public async Task PassedOnWrongRevision_SaysNothingAboutFrozenTree()
    {
        ChangeReport report = ChangeReport.Create(
            "run-1", Base, Head,
            [ChangedPathEntry.Create("src/Foo.cs", "M")],
            [RecordedCheck.Passed("build", "dotnet build", Base, ["PATH"])],
            0, 600, 50, false);
        await Assert.That(report.Verified.Count).IsEqualTo(0);
        await Assert.That(report.ExitCode).IsEqualTo(ChangeReport.ExitAllPassed);
        bool named = false;
        for (int i = 0; i < report.NotVerified.Count; i++)
        {
            if (report.NotVerified[i].What == "build")
                named = true;
        }
        await Assert.That(named).IsTrue();
    }

    [Test]
    public async Task ZeroChecks_YieldsExplicitNoChecksDeclared()
    {
        ChangeReport report = EmptyReport();
        bool named = false;
        for (int i = 0; i < report.NotVerified.Count; i++)
        {
            if (report.NotVerified[i].Reason == "no checks declared")
                named = true;
        }
        await Assert.That(named).IsTrue();
        await Assert.That(report.ExitCode).IsEqualTo(ChangeReport.ExitAllPassed);
    }

    [Test]
    public async Task ExternalSideEffects_AlwaysListed()
    {
        ChangeReport report = EmptyReport();
        bool listed = false;
        for (int i = 0; i < report.NotVerified.Count; i++)
        {
            if (report.NotVerified[i].What == "external side effects"
                && report.NotVerified[i].Reason == "not observable")
                listed = true;
        }
        await Assert.That(listed).IsTrue();
        await Assert.That(ChangeReport.ExternalSideEffectsEntry.Contains("not observable")).IsTrue();
    }

    [Test]
    public async Task KnownRisks_TriggersEachCondition()
    {
        ChangeReport ignored = ChangeReport.Create(
            "r", Base, Head,
            [ChangedPathEntry.Create("a.cs", "A")],
            [RecordedCheck.Passed("b", "b", Head, ["PATH"])],
            3, 600, 50, false);
        await Assert.That(ignored.KnownRisks.Count > 0).IsTrue();

        await Assert.That(EmptyReport().KnownRisks.Count > 0).IsTrue();

        ChangeReport dirty = ChangeReport.Create(
            "r", Base, Head,
            [ChangedPathEntry.Create("a.cs", "A")],
            [RecordedCheck.Passed("b", "b", Head, ["PATH"])],
            0, 600, 50, true);
        await Assert.That(dirty.KnownRisks.Count > 0).IsTrue();

        ChangeReport timedOut = ChangeReport.Create(
            "r", Base, Head,
            [ChangedPathEntry.Create("a.cs", "A")],
            [RecordedCheck.DidNotRun("b", "b", "killed", Head, ["PATH"], timedOut: true)],
            0, 600, 50, false);
        await Assert.That(timedOut.KnownRisks.Count > 0).IsTrue();
    }

    [Test]
    public async Task KnownRisks_CleanRun_IsExplicitlyEmpty()
    {
        ChangeReport report = ChangeReport.Create(
            "r", Base, Head,
            [ChangedPathEntry.Create("a.cs", "A")],
            [RecordedCheck.Passed("b", "b", Head, ["PATH"])],
            0, 600, 50, false);
        await Assert.That(report.KnownRisks.Count).IsEqualTo(0);
        string json = System.Text.Encoding.UTF8.GetString(report.ToJsonBytes());
        await Assert.That(json.Contains("\"knownRisks\": []")).IsTrue();
    }

    [Test]
    public async Task Determinism_SameArtifacts_ByteIdenticalJson()
    {
        ChangeReport first = ChangeReport.Create(
            "run-9", Base, Head,
            [ChangedPathEntry.Create("b.cs", "M"), ChangedPathEntry.Create("a.cs", "A")],
            [RecordedCheck.Passed("z", "z-cmd", Head, ["PATH", "HOME"]),
             RecordedCheck.Failed("a", "a-cmd", 2, Head, ["PATH"])],
            1, 600, 50, true);
        ChangeReport second = ChangeReport.Create(
            "run-9", Base, Head,
            [ChangedPathEntry.Create("a.cs", "A"), ChangedPathEntry.Create("b.cs", "M")],
            [RecordedCheck.Failed("a", "a-cmd", 2, Head, ["PATH"]),
             RecordedCheck.Passed("z", "z-cmd", Head, ["HOME", "PATH"])],
            1, 600, 50, true);
        byte[] a = first.ToJsonBytes();
        byte[] b = second.ToJsonBytes();
        await Assert.That(a.Length).IsEqualTo(b.Length);
        await Assert.That(a.SequenceEqual(b)).IsTrue();
    }

    [Test]
    public async Task NoPublicWritePath_ForVerifiedAndNotVerified()
    {
        // The agent has no API to add or remove Verified / NotVerified entries:
        // get-only properties, no public constructor — every instance goes
        // through Create, which is what derives. A setter or a public ctor
        // would let a caller author the verdict instead of deriving it.
        string[] names = ["Verified", "NotVerified", "KnownRisks"];
        for (int i = 0; i < names.Length; i++)
        {
            System.Reflection.PropertyInfo? prop = typeof(ChangeReport).GetProperty(names[i]);
            await Assert.That(prop).IsNotNull();
            await Assert.That(prop!.CanRead).IsTrue();
            await Assert.That(prop.CanWrite).IsFalse().Because(
                "a settable " + names[i] + " lets a caller author the report instead of deriving it "
                + "from the S3/S4 artifacts, which is the failure mode the slice exists to prevent.");
        }

        System.Reflection.ConstructorInfo[] publicCtors = typeof(ChangeReport)
            .GetConstructors(System.Reflection.BindingFlags.Public
                | System.Reflection.BindingFlags.Instance
                | System.Reflection.BindingFlags.DeclaredOnly);

        await Assert.That(publicCtors).IsEmpty().Because(
            "Create is the only way to obtain a ChangeReport, and Create is the only place "
            + "the derivation lives. A public constructor turns the slice's rule into a convention.");

        System.Reflection.MethodInfo[] methods = typeof(ChangeReport).GetMethods(
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
        for (int i = 0; i < methods.Length; i++)
        {
            string name = methods[i].Name;
            bool isWriter = name.StartsWith("Add", StringComparison.Ordinal)
                || name.StartsWith("Remove", StringComparison.Ordinal)
                || name.StartsWith("Set", StringComparison.Ordinal)
                || name.StartsWith("Clear", StringComparison.Ordinal);
            await Assert.That(isWriter).IsFalse();
        }
    }

    [Test]
    public async Task ExitCodes_AreDistinct()
    {
        await Assert.That(ChangeReport.ExitAllPassed).IsEqualTo(0);
        await Assert.That(ChangeReport.ExitCheckFailed).IsEqualTo(1);
        await Assert.That(ChangeReport.ExitCannotProduce).IsEqualTo(2);
    }
}
