using System;
using System.IO;
using System.Globalization;
using Harbor.App.Cli.Commands;
using Harbor.App.Cli.Logging;

namespace Harbor.App.Cli.Tests;

/// <summary>
///     G3: <c>harbor logs --spans</c> reads the visible telemetry export
///     without starting a listener. These tests cover the parser and the
///     command's filter/sort/options against a real temp file.
/// </summary>
public class SpanLogReaderTests
{
    private const string SlowLine =
        "activity llm.stream duration_ms=\"187.5\" status=\"Ok\" llm.model=\"m1\"";
    private const string MediumLine =
        "activity tool.execute duration_ms=\"62.125\" status=\"Ok\" tool.name=\"read\"";
    private const string FastLine =
        "activity agent.turn duration_ms=\"3.25\" status=\"Ok\" agent.name=\"code\"";

    [Test]
    public async Task TryParse_ReadsNameDurationAndStatus()
    {
        bool ok = SpanLogReader.TryParse(SlowLine, out SpanLogReader.SpanEntry entry);

        await Assert.That(ok).IsTrue();
        await Assert.That(entry.Name).IsEqualTo("llm.stream");
        await Assert.That(entry.DurationMs).IsEqualTo(187.5);
        await Assert.That(entry.Status).IsEqualTo("Ok");
        await Assert.That(entry.Line).IsEqualTo(SlowLine);
    }

    [Test]
    public async Task TryParse_IgnoresMetricsAndTruncatedSpans()
    {
        await Assert.That(SpanLogReader.TryParse("metric llm.tokens value=\"10\"", out _)).IsFalse();
        await Assert.That(SpanLogReader.TryParse("activity broken duration_ms=\"12", out _)).IsFalse();
        await Assert.That(SpanLogReader.TryParse("", out _)).IsFalse();
    }

    [Test]
    public async Task Read_MissingFile_ReturnsEmpty()
    {
        string path = TempPath();

        IReadOnlyList<SpanLogReader.SpanEntry> entries = SpanLogReader.Read(path);

        await Assert.That(entries.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Spans_Command_FiltersSortsAndLimits()
    {
        string path = TempPath();
        File.WriteAllLines(path, [FastLine, SlowLine, MediumLine, "metric llm.tokens value=\"1\""]);
        try
        {
            using StringWriter output = new();
            using StringWriter error = new();
            LogsCommand command = new(output, error, path);

            int exit = await command.ExecuteAsync(["--spans", "--slow", "50ms", "--top", "2"]);

            await Assert.That(exit).IsEqualTo(0);
            await Assert.That(error.ToString()).IsEqualTo(string.Empty);
            string text = output.ToString();
            await Assert.That(text).Contains("2 at or above 50.0 ms");
            int first = text.IndexOf("llm.stream", StringComparison.Ordinal);
            int second = text.IndexOf("tool.execute", StringComparison.Ordinal);
            await Assert.That(first).IsGreaterThan(-1);
            await Assert.That(second).IsGreaterThan(first);
            await Assert.That(text).DoesNotContain("agent.turn");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public async Task Spans_Command_MissingExport_IsNotAnError()
    {
        string path = TempPath();
        using StringWriter output = new();
        using StringWriter error = new();
        LogsCommand command = new(output, error, path);

        int exit = await command.ExecuteAsync(["--spans"]);

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(output.ToString()).Contains("No telemetry export yet");
        await Assert.That(error.ToString()).IsEqualTo(string.Empty);
    }

    [Test]
    public async Task Spans_Command_BadOption_FailsClosed()
    {
        using StringWriter output = new();
        using StringWriter error = new();
        LogsCommand command = new(output, error, TempPath());

        int exit = await command.ExecuteAsync(["--spans", "--top", "0"]);

        await Assert.That(exit).IsEqualTo(1);
        await Assert.That(error.ToString()).Contains("--top needs a positive integer");
    }

    private static string TempPath() => Path.Combine(
        Path.GetTempPath(),
        "harbor-spans-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture) + ".txt");
}
