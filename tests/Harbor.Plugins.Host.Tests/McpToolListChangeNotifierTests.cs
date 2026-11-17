using System.Text.Json;
using Harbor.Plugins.Host;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;

namespace Harbor.Plugins.Host.Tests;

/// <summary>
///     #2228: the debounce/generation behavior behind
///     <c>notifications/tools/list_changed</c>. Timing is driven by a fake
///     <see cref="TimeProvider" />, so no test sleeps.
/// </summary>
public class McpToolListChangeNotifierTests
{
    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(100);

    [Test]
    public async Task SingleChange_WaitsForQuietWindow_ThenEmitsOnce()
    {
        var clock = new ManualClock();
        var notifier = new McpToolListChangeNotifier(Debounce, clock);

        notifier.SignalChange();
        await Assert.That(notifier.TryTake(out string? early)).IsFalse()
            .Because("the debounce window has not elapsed yet");
        await Assert.That(early).IsNull();

        clock.Advance(Debounce);
        await Assert.That(notifier.TryTake(out string? frame)).IsTrue();
        await Assert.That(frame).IsNotNull();
        await AssertFrame(frame!, generation: 1);

        await Assert.That(notifier.TryTake(out _)).IsFalse()
            .Because("one signal yields exactly one notification");
    }

    [Test]
    public async Task BurstOfChanges_CollapsesIntoOneNotification_WithLatestGeneration()
    {
        var clock = new ManualClock();
        var notifier = new McpToolListChangeNotifier(Debounce, clock);

        for (int i = 0; i < 10; i++)
        {
            notifier.SignalChange();
            clock.Advance(TimeSpan.FromMilliseconds(10));
        }

        await Assert.That(notifier.CurrentGeneration).IsEqualTo(10);
        await Assert.That(notifier.TryTake(out _)).IsFalse()
            .Because("the last signal is still inside the debounce window");

        clock.Advance(Debounce);
        await Assert.That(notifier.TryTake(out string? frame)).IsTrue();
        await AssertFrame(frame!, generation: 10);
        await Assert.That(notifier.TryTake(out _)).IsFalse()
            .Because("the whole burst must collapse into a single frame");
    }

    [Test]
    public async Task ChangeAfterWindow_EmitsAnotherNotification_WithHigherGeneration()
    {
        var clock = new ManualClock();
        var notifier = new McpToolListChangeNotifier(Debounce, clock);

        notifier.SignalChange();
        clock.Advance(Debounce);
        await Assert.That(notifier.TryTake(out string? first)).IsTrue();
        await AssertFrame(first!, generation: 1);

        notifier.SignalChange();
        clock.Advance(Debounce);
        await Assert.That(notifier.TryTake(out string? second)).IsTrue();
        await AssertFrame(second!, generation: 2);
    }

    [Test]
    public async Task DebounceThatSlips_BecauseSignalsKeepComing_StillEmitsOne()
    {
        var clock = new ManualClock();
        var notifier = new McpToolListChangeNotifier(Debounce, clock);

        // A steady chatter: every signal lands just before the window closes.
        for (int i = 0; i < 5; i++)
        {
            notifier.SignalChange();
            clock.Advance(Debounce - TimeSpan.FromMilliseconds(5));
            await Assert.That(notifier.TryTake(out _)).IsFalse();
        }

        clock.Advance(Debounce);
        await Assert.That(notifier.TryTake(out string? frame)).IsTrue();
        await AssertFrame(frame!, generation: 5);
    }

    private static async Task AssertFrame(string frame, long generation)
    {
        using var doc = JsonDocument.Parse(frame);
        var root = doc.RootElement;
        await Assert.That(root.GetProperty("jsonrpc").GetString()).IsEqualTo("2.0");
        await Assert.That(root.GetProperty("method").GetString()).IsEqualTo("notifications/tools/list_changed");
        await Assert.That(root.GetProperty("params").GetProperty("generation").GetInt64()).IsEqualTo(generation);
        await Assert.That(frame.EndsWith("\n")).IsTrue()
            .Because("the frame must be NDJSON-terminated for stdout streaming");
    }

    /// <summary>Manual clock so debounce is deterministic and sleep-free.</summary>
    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UnixEpoch;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan delta) => _now += delta;
    }
}
