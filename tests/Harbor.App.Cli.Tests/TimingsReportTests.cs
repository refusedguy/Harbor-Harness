using System.Diagnostics;
using System.Globalization;
using Harbor.App.Cli.Commands;

namespace Harbor.App.Cli.Tests;

/// <summary>
///     #2675 item 022: <c>harbor ask --timings</c> splits the one-shot run
///     into sequential phases and reports overlapping background work apart
///     from total. The flag is a pure CLI concern: these tests pin both the
///     argument stripping and the report shape without building a host.
/// </summary>
public class TimingsReportTests
{
    [Test]
    public async Task ExtractTimingsArg_StripsFlagAndKeepsPromptWords()
    {
        bool timings = CliArgs.ExtractTimingsArg(["hello", "--timings", "world"], out string[] remaining);

        await Assert.That(timings).IsTrue();
        await Assert.That(string.Join(' ', remaining)).IsEqualTo("hello world");
    }

    [Test]
    public async Task ExtractTimingsArg_NoFlag_LeavesArgsUntouched()
    {
        bool timings = CliArgs.ExtractTimingsArg(["hello", "world"], out string[] remaining);

        await Assert.That(timings).IsFalse();
        await Assert.That(string.Join(' ', remaining)).IsEqualTo("hello world");
    }

    [Test]
    public async Task Format_PrintsEveryPhaseAndTheTotal()
    {
        string report = TimingsReport.Format(
        [
            new TimingsReport.Phase("startup", TimeSpan.FromMilliseconds(12.5)),
            new TimingsReport.Phase("auth/connect", TimeSpan.FromMilliseconds(34)),
            new TimingsReport.Phase("execution", TimeSpan.FromMilliseconds(56.7)),
            new TimingsReport.Phase("cleanup", TimeSpan.FromMilliseconds(1.2)),
        ], "skill freshness seeding");

        await Assert.That(report).Contains("timings:");
        await Assert.That(report).Contains("startup: 12.5 ms");
        await Assert.That(report).Contains("auth/connect: 34.0 ms");
        await Assert.That(report).Contains("execution: 56.7 ms");
        await Assert.That(report).Contains("cleanup: 1.2 ms");
        await Assert.That(report).Contains("total: 104.4 ms");
        await Assert.That(report).Contains("overlapping work (excluded from total):");
        await Assert.That(report).Contains("skill freshness seeding");
    }

    [Test]
    public async Task Format_DoesNotFoldOverlappingWorkIntoTotal()
    {
        string report = TimingsReport.Format(
        [
            new TimingsReport.Phase("execution", TimeSpan.FromMilliseconds(10)),
            new TimingsReport.Phase("cleanup", TimeSpan.FromMilliseconds(2)),
        ], "50 ms of background work");

        await Assert.That(report).Contains("total: 12.0 ms");
        await Assert.That(report.Contains("62.0 ms")).IsFalse();
    }

    [Test]
    public async Task Elapsed_ConvertsTimestampDeltaToSeconds()
    {
        long start = 1000;

        TimeSpan elapsed = TimingsReport.Elapsed(start, start + Stopwatch.Frequency);

        await Assert.That(elapsed.TotalSeconds.ToString("F2", CultureInfo.InvariantCulture)).IsEqualTo("1.00");
    }
}
