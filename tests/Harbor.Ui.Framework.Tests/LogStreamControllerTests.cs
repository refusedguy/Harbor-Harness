using Harbor.Ui.Framework.Diagnostics;
using Microsoft.Extensions.Logging;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;

namespace Harbor.Ui.Framework.Tests;

/// <summary>
///     Issue #2698, primitive P10: a log viewport needs one shared state object
///     for follow, level/text filters, wrap and timestamp. These tests describe
///     the observable behaviour of that state, not how the ring is indexed.
/// </summary>
public sealed class LogStreamControllerTests
{
    private static readonly DateTimeOffset Stamp = new(2026, 11, 18, 12, 0, 0, TimeSpan.Zero);

    private static void Append(
        LogStreamController stream,
        string message,
        LogLevel level = LogLevel.Information,
        string category = "Harbor.Net.Client") =>
        stream.Append(new DiagnosticEntry(Stamp, level, category, message));

    private static void AppendNumbered(LogStreamController stream, int count)
    {
        for (int i = 0; i < count; i++)
        {
            Append(stream, "line-" + i);
        }
    }

    [Test]
    public async Task Ring_EvictsOldestWhenFull()
    {
        var stream = new LogStreamController(3);
        AppendNumbered(stream, 5);

        IReadOnlyList<DiagnosticEntry> entries = stream.Matching();
        await Assert.That(stream.Capacity).IsEqualTo(3);
        await Assert.That(stream.Count).IsEqualTo(3);
        await Assert.That(entries[0].Message).IsEqualTo("line-2");
        await Assert.That(entries[2].Message).IsEqualTo("line-4");
    }

    [Test]
    public async Task Matching_LevelFilter_ExcludesBelowMinimum()
    {
        var stream = new LogStreamController();
        Append(stream, "verbose", LogLevel.Debug);
        Append(stream, "notice", LogLevel.Information);
        Append(stream, "bad", LogLevel.Error);

        stream.MinLevel = LogLevel.Warning;
        IReadOnlyList<DiagnosticEntry> entries = stream.Matching();
        await Assert.That(entries.Count).IsEqualTo(1);
        await Assert.That(entries[0].Message).IsEqualTo("bad");
    }

    [Test]
    public async Task Matching_TextFilter_IsCaseInsensitiveAcrossCategoryAndMessage()
    {
        var stream = new LogStreamController();
        Append(stream, "connected");
        Append(stream, "NETWORK reset");

        stream.TextFilter = "net";
        await Assert.That(stream.Matching().Count).IsEqualTo(2);

        stream.TextFilter = "RESET";
        IReadOnlyList<DiagnosticEntry> hits = stream.Matching();
        await Assert.That(hits[0].Message).IsEqualTo("NETWORK reset");

        stream.TextFilter = "absent";
        await Assert.That(stream.Matching()).IsEmpty();
    }

    [Test]
    public async Task Matching_RegexFilter_RejectsInvalidPatternAndAcceptsValidOne()
    {
        var stream = new LogStreamController();
        Append(stream, "ERR-1", LogLevel.Error);
        Append(stream, "OK-2", LogLevel.Information);

        stream.UseRegex = true;
        stream.TextFilter = "[";
        await Assert.That(stream.Matching()).IsEmpty();

        stream.TextFilter = "^err";
        IReadOnlyList<DiagnosticEntry> hits = stream.Matching();
        await Assert.That(hits.Count).IsEqualTo(1);
        await Assert.That(hits[0].Message).IsEqualTo("ERR-1");
    }

    [Test]
    public async Task Render_FollowTail_ShowsNewestRowsAndTracksAppends()
    {
        var stream = new LogStreamController();
        AppendNumbered(stream, 5);
        IReadOnlyList<string> first = stream.Render(width: 80, maxRows: 2);

        await Assert.That(first.Count).IsEqualTo(2);
        await Assert.That(first[0]).Contains("line-3");
        await Assert.That(first[1]).Contains("line-4");
        await Assert.That(stream.FollowTail).IsTrue();

        Append(stream, "line-5");
        IReadOnlyList<string> second = stream.Render(80, 2);
        await Assert.That(second[1]).Contains("line-5");
    }

    [Test]
    public async Task Scroll_UpBreaksFollowDownRestoresItAndTailRebinds()
    {
        var stream = new LogStreamController();
        AppendNumbered(stream, 5);

        stream.ScrollUp(2);
        await Assert.That(stream.FollowTail).IsFalse();
        await Assert.That(stream.ScrollOffset).IsEqualTo(2);
        IReadOnlyList<string> older = stream.Render(80, 2);
        await Assert.That(older[0]).Contains("line-2");

        stream.ScrollDown(10);
        await Assert.That(stream.FollowTail).IsTrue();
        await Assert.That(stream.ScrollOffset).IsEqualTo(0);
        await Assert.That(stream.Render(80, 1)[0]).Contains("line-4");

        stream.ScrollUp(4);
        stream.ScrollToTail();
        await Assert.That(stream.ScrollOffset).IsEqualTo(0);
        await Assert.That(stream.Render(80, 1)[0]).Contains("line-4");
    }

    [Test]
    public async Task Render_HideTimestamp_DropsClockColumnButKeepsLevelAndMessage()
    {
        var stream = new LogStreamController();
        Append(stream, "plain", LogLevel.Warning);

        await Assert.That(stream.Render(80, 1)[0]).Contains("WARN");

        stream.ShowTimestamp = false;
        string hidden = stream.Render(80, 1)[0];
        await Assert.That(hidden).DoesNotContain("12:");
        await Assert.That(hidden).Contains("WARN");
        await Assert.That(hidden).Contains("plain");
    }

    [Test]
    public async Task Render_Wrap_SplitsLongMessageWithinViewportWidth()
    {
        var stream = new LogStreamController();
        Append(stream, new string('x', 60));

        stream.Wrap = true;
        IReadOnlyList<string> lines = stream.Render(width: 20, maxRows: 10);
        await Assert.That(lines.Count).IsGreaterThan(1);
        foreach (string line in lines)
        {
            await Assert.That(line.Length).IsLessThanOrEqualTo(20);
        }
    }

    [Test]
    public async Task Render_Clip_TruncatesLongMessageToWidth()
    {
        var stream = new LogStreamController();
        Append(stream, new string('y', 60));

        IReadOnlyList<string> lines = stream.Render(width: 20, maxRows: 1);
        await Assert.That(lines.Count).IsEqualTo(1);
        await Assert.That(lines[0].Length).IsEqualTo(20);
    }

    [Test]
    public async Task Clear_ResetsRingAndFollowState()
    {
        var stream = new LogStreamController();
        Append(stream, "one");
        stream.ScrollUp(1);

        stream.Clear();

        await Assert.That(stream.Count).IsEqualTo(0);
        await Assert.That(stream.Matching()).IsEmpty();
        await Assert.That(stream.Render(80, 5)).IsEmpty();
        await Assert.That(stream.FollowTail).IsTrue();
        await Assert.That(stream.ScrollOffset).IsEqualTo(0);
    }
}
