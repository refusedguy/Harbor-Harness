using Harbor.Tui.CellForge.Widgets;

namespace Harbor.Tui.CellForge.Tests;

public class RetryCountdownTests
{
    [Test]
    public async Task Line_FormatsAttemptAndSeconds()
    {
        await Assert.That(RetryCountdown.Line(2, 5, 4)).IsEqualTo("retry 2/5 in 4s");
        await Assert.That(RetryCountdown.Line(1, 3, 1)).IsEqualTo("retry 1/3 in 1s");
    }

    [Test]
    public async Task Line_NegativeSeconds_ClampedToZero()
    {
        await Assert.That(RetryCountdown.Line(1, 5, -7)).IsEqualTo("retry 1/5 in 0s");
        await Assert.That(RetryCountdown.Line(0, 5, 9)).IsEqualTo("retry 1/5 in 9s");
    }

    [Test]
    public async Task Segments_WideBar_ProportionalFill()
    {
        var (line, bar) = RetryCountdown.Segments(1, 4, secondsRemaining: 8, totalSeconds: 10, barWidth: 10);
        await Assert.That(line).IsEqualTo("retry 1/4 in 8s");
        await Assert.That(bar).IsEqualTo("████████░░");
    }

    [Test]
    public async Task Segments_NarrowBar_Suppressed()
    {
        var (line, bar) = RetryCountdown.Segments(1, 4, 8, 10, barWidth: 2);
        await Assert.That(bar).IsEqualTo(string.Empty);
        await Assert.That(line).IsEqualTo("retry 1/4 in 8s");
    }

    [Test]
    public async Task Segments_NeverExceedsWidth()
    {
        var (_, bar) = RetryCountdown.Segments(1, 9, 999, 3, barWidth: 5);
        await Assert.That(bar).IsEqualTo("█████");
    }

    [Test]
    public async Task Bar_EmptyFill_AllTrack()
    {
        await Assert.That(RetryCountdown.Bar(0, 4)).IsEqualTo("░░░░");
    }

    [Test]
    public async Task Bar_ZeroWidth_Empty()
    {
        await Assert.That(RetryCountdown.Bar(3, 0)).IsEqualTo(string.Empty);
        await Assert.That(RetryCountdown.Bar(3, -1)).IsEqualTo(string.Empty);
    }

    [Test]
    public async Task BackoffSeconds_DoublesAndCaps()
    {
        await Assert.That(RetryCountdown.BackoffSeconds(1)).IsEqualTo(1);
        await Assert.That(RetryCountdown.BackoffSeconds(2)).IsEqualTo(2);
        await Assert.That(RetryCountdown.BackoffSeconds(3)).IsEqualTo(4);
        await Assert.That(RetryCountdown.BackoffSeconds(4, baseSeconds: 1, maxSeconds: 5)).IsEqualTo(5);
        await Assert.That(RetryCountdown.BackoffSeconds(20)).IsEqualTo(60);
    }

    [Test]
    public async Task BackoffSeconds_AttemptBelowOne_Throws()
    {
        await Assert.That(() => RetryCountdown.BackoffSeconds(0)).Throws<ArgumentOutOfRangeException>();
    }
}
