using System;
using Harbor.Tui.CellForge.Generic.Widgets;
using TUnit.Core;

namespace Harbor.Tui.CellForge.Tests;

// Painting reads the global TerminalColorPalette, so these serialize with other paint tests.
[NotInParallel("pty")]
public class FocusTooltipTests
{
    private sealed class Target(string id) : EngineInput.IFocusTarget
    {
        public string Id { get; } = id;
        public bool Focused { get; private set; }

        public void OnFocusChanged(bool focused) => Focused = focused;
    }

    private static (EngineInput.FocusRouter Router, Target Target, ManualTimeProvider Clock, FocusTooltip Tip) Harness(
        Func<EngineInput.IFocusTarget, string?>? describe = null,
        TimeSpan? delay = null,
        TimeSpan? timeout = null)
    {
        var clock = new ManualTimeProvider();
        var router = new EngineInput.FocusRouter();
        var target = new Target("save");
        router.Add(target);
        router.FocusById("save"); // Add only joins the Tab order; focus is explicit.
        var tip = new FocusTooltip(router, describe ?? (_ => "Save the file"), delay, timeout, clock);
        return (router, target, clock, tip);
    }

    // ------------------------------------------------------------ contract --

    [Test]
    public async Task FocusedTarget_HintAppearsOnlyAfterDelay()
    {
        var (_, _, clock, tip) = Harness(delay: TimeSpan.FromMilliseconds(400));
        int changes = 0;
        tip.Changed += (_, _) => changes++;

        await Assert.That(tip.Tick()).IsFalse(); // focus is sampled now, the clock has not moved
        await Assert.That(tip.IsVisible).IsFalse();
        await Assert.That(tip.TargetId).IsEqualTo("save");

        clock.AdvanceMs(399);
        await Assert.That(tip.Tick()).IsFalse();
        await Assert.That(tip.IsVisible).IsFalse();

        clock.AdvanceMs(1);
        await Assert.That(tip.Tick()).IsTrue();
        await Assert.That(tip.IsVisible).IsTrue();
        await Assert.That(tip.Text).IsEqualTo("Save the file"); // external text
        await Assert.That(changes).IsEqualTo(1);
    }

    [Test]
    public async Task RepeatedTicksAtSameInstant_NeverAdvanceTheHint()
    {
        var (_, _, clock, tip) = Harness(delay: TimeSpan.FromMilliseconds(200));

        for (int i = 0; i < 10; i++)
        {
            await Assert.That(tip.Tick()).IsFalse();
        }

        await Assert.That(tip.IsVisible).IsFalse();
        clock.AdvanceMs(200);
        await Assert.That(tip.Tick()).IsTrue();
        for (int i = 0; i < 10; i++)
        {
            await Assert.That(tip.Tick()).IsFalse();
        }

        await Assert.That(tip.IsVisible).IsTrue();
    }

    [Test]
    public async Task FocusChange_HidesImmediately_AndRestartsDelay()
    {
        var (router, _, clock, tip) = Harness(
            describe: t => t.Id == "save" ? "Save the file" : "Open a file",
            delay: TimeSpan.FromMilliseconds(200),
            timeout: TimeSpan.FromMilliseconds(5000));
        var second = new Target("open");
        router.Add(second);

        await Assert.That(tip.Tick()).IsFalse(); // the tooltip discovers focus on its first tick
        clock.AdvanceMs(200);
        await Assert.That(tip.Tick()).IsTrue();
        await Assert.That(tip.Text).IsEqualTo("Save the file");

        await Assert.That(router.FocusById("open")).IsTrue();
        await Assert.That(tip.Tick()).IsTrue(); // focus change hides on the very next tick
        await Assert.That(tip.IsVisible).IsFalse();
        await Assert.That(tip.Text).IsEmpty();
        await Assert.That(tip.TargetId).IsEqualTo("open");

        clock.AdvanceMs(199);
        await Assert.That(tip.Tick()).IsFalse();
        clock.AdvanceMs(1);
        await Assert.That(tip.Tick()).IsTrue();
        await Assert.That(tip.Text).IsEqualTo("Open a file");
    }

    [Test]
    public async Task Blur_HidesAndCancelsBothVisibleAndPendingHints()
    {
        var (router, _, clock, tip) = Harness(delay: TimeSpan.FromMilliseconds(200));
        await Assert.That(tip.Tick()).IsFalse();
        clock.AdvanceMs(200);
        await Assert.That(tip.Tick()).IsTrue();

        router.Clear();
        await Assert.That(tip.Tick()).IsTrue();
        await Assert.That(tip.IsVisible).IsFalse();
        await Assert.That(tip.TargetId).IsNull();
        clock.AdvanceMs(5000);
        await Assert.That(tip.Tick()).IsFalse();
        await Assert.That(tip.IsVisible).IsFalse();
    }

    [Test]
    public async Task Timeout_HidesHint_AndDoesNotReshowWhileFocusIsHeld()
    {
        var (_, _, clock, tip) = Harness(
            delay: TimeSpan.FromMilliseconds(200),
            timeout: TimeSpan.FromMilliseconds(300));

        await Assert.That(tip.Tick()).IsFalse();
        clock.AdvanceMs(200);
        await Assert.That(tip.Tick()).IsTrue();
        clock.AdvanceMs(299);
        await Assert.That(tip.Tick()).IsFalse();
        await Assert.That(tip.IsVisible).IsTrue();

        clock.AdvanceMs(1);
        await Assert.That(tip.Tick()).IsTrue();
        await Assert.That(tip.IsVisible).IsFalse();
        clock.AdvanceMs(10_000);
        await Assert.That(tip.Tick()).IsFalse();
        await Assert.That(tip.IsVisible).IsFalse();
    }

    [Test]
    public async Task EmptyDescription_SuppressesHintUntilFocusMoves()
    {
        bool available = false;
        var (router, _, clock, tip) = Harness(
            describe: t => t.Id == "save"
                ? (available ? "Available now" : null)
                : "Second hint",
            delay: TimeSpan.FromMilliseconds(200));
        var second = new Target("other");
        router.Add(second);

        await Assert.That(tip.Tick()).IsFalse();
        clock.AdvanceMs(200);
        await Assert.That(tip.Tick()).IsFalse();
        available = true;
        clock.AdvanceMs(2000);
        await Assert.That(tip.Tick()).IsFalse(); // suppressed for this focus
        await Assert.That(tip.IsVisible).IsFalse();

        router.FocusById("other");
        await Assert.That(tip.Tick()).IsFalse();
        clock.AdvanceMs(200);
        await Assert.That(tip.Tick()).IsTrue();
        await Assert.That(tip.Text).IsEqualTo("Second hint");
    }

    [Test]
    public async Task ZeroDelayAndZeroTimeout_ShowAtOnce_AndStickUntilBlur()
    {
        var (router, _, clock, tip) = Harness(
            delay: TimeSpan.Zero,
            timeout: TimeSpan.Zero);

        await Assert.That(tip.Tick()).IsTrue();
        await Assert.That(tip.IsVisible).IsTrue();
        clock.AdvanceMs(100_000);
        await Assert.That(tip.Tick()).IsFalse();
        await Assert.That(tip.IsVisible).IsTrue();

        router.Clear();
        await Assert.That(tip.Tick()).IsTrue();
        await Assert.That(tip.IsVisible).IsFalse();
    }

    [Test]
    public async Task Text_IsSnapshottedAtShowTime()
    {
        string text = "Original";
        var (router, _, clock, tip) = Harness(
            describe: t => t.Id == "save" ? text : "Other",
            delay: TimeSpan.FromMilliseconds(200));
        var second = new Target("other");
        router.Add(second);

        await Assert.That(tip.Tick()).IsFalse();
        clock.AdvanceMs(200);
        await Assert.That(tip.Tick()).IsTrue();
        text = "Changed by host";
        await Assert.That(tip.Tick()).IsFalse();
        await Assert.That(tip.Text).IsEqualTo("Original");

        router.FocusById("other");
        await Assert.That(tip.Tick()).IsTrue();
        router.FocusById("save");
        await Assert.That(tip.Tick()).IsFalse();
        clock.AdvanceMs(200);
        await Assert.That(tip.Tick()).IsTrue();
        await Assert.That(tip.Text).IsEqualTo("Changed by host");
    }

    [Test]
    public async Task Reset_CancelsPendingHint_AndRestartsDelay()
    {
        var (_, _, clock, tip) = Harness(delay: TimeSpan.FromMilliseconds(200));
        await Assert.That(tip.Tick()).IsFalse();
        clock.AdvanceMs(200);
        await Assert.That(tip.Tick()).IsTrue();

        await Assert.That(tip.Reset()).IsTrue();
        await Assert.That(tip.IsVisible).IsFalse();
        await Assert.That(tip.Tick()).IsFalse();
        clock.AdvanceMs(199);
        await Assert.That(tip.Tick()).IsFalse();
        clock.AdvanceMs(1);
        await Assert.That(tip.Tick()).IsTrue();
        await Assert.That(tip.Text).IsEqualTo("Save the file");
    }

    [Test]
    public async Task Changed_RaisedOnShowFocusChangeAndTimeout()
    {
        var (router, _, clock, tip) = Harness(
            describe: t => t.Id == "save" ? "Save the file" : "Open a file",
            delay: TimeSpan.FromMilliseconds(200),
            timeout: TimeSpan.FromMilliseconds(300));
        var second = new Target("open");
        router.Add(second);
        int changes = 0;
        tip.Changed += (_, _) => changes++;

        await Assert.That(tip.Tick()).IsFalse();
        clock.AdvanceMs(200);
        await Assert.That(tip.Tick()).IsTrue();
        router.FocusById("open");
        await Assert.That(tip.Tick()).IsTrue();
        clock.AdvanceMs(200);
        await Assert.That(tip.Tick()).IsTrue();
        clock.AdvanceMs(300);
        await Assert.That(tip.Tick()).IsTrue();

        await Assert.That(changes).IsEqualTo(4);
    }

    // --------------------------------------------------------------- golden --

    [Test]
    public async Task Golden_BorderedHint_PaintsExternalText()
    {
        var (_, _, _, tip) = Harness(delay: TimeSpan.Zero, timeout: TimeSpan.Zero);
        await Assert.That(tip.Tick()).IsTrue();
        var buffer = TestRender.Buffer(24, 3);

        tip.Paint(buffer, new Rect(0, 0, 24, 3));

        await Assert.That(ScreenFixtures.Row(buffer, 0, 24)).IsEqualTo("┌" + new string('─', 22) + "┐");
        await Assert.That(ScreenFixtures.Row(buffer, 1, 24)).IsEqualTo("│ " + "Save the file".PadRight(20) + " │");
        await Assert.That(ScreenFixtures.Row(buffer, 2, 24)).IsEqualTo("└" + new string('─', 22) + "┘");
    }

    [Test]
    public async Task Golden_LongText_IsClippedWithEllipsis()
    {
        var (_, _, _, tip) = Harness(
            describe: _ => "ABCDEFGHIJKLMNOPQRSTUVWXYZ",
            delay: TimeSpan.Zero,
            timeout: TimeSpan.Zero);
        await Assert.That(tip.Tick()).IsTrue();
        var buffer = TestRender.Buffer(14, 3);

        tip.Paint(buffer, new Rect(0, 0, 14, 3));

        await Assert.That(ScreenFixtures.Row(buffer, 0, 14)).IsEqualTo("┌" + new string('─', 12) + "┐");
        await Assert.That(ScreenFixtures.Row(buffer, 1, 14)).IsEqualTo("│ ABCDEFGHI… │");
        await Assert.That(ScreenFixtures.Row(buffer, 2, 14)).IsEqualTo("└" + new string('─', 12) + "┘");
    }

    [Test]
    public async Task Golden_HiddenOrTooSmall_PaintsNothing()
    {
        var (_, _, _, hidden) = Harness(delay: TimeSpan.FromMilliseconds(200));
        await Assert.That(hidden.Tick()).IsFalse();
        var empty = TestRender.Buffer(24, 3);

        hidden.Paint(empty, new Rect(0, 0, 24, 3));

        await Assert.That(ScreenFixtures.Row(empty, 0, 24)).IsEqualTo(new string(' ', 24));

        var (_, _, _, visible) = Harness(delay: TimeSpan.Zero, timeout: TimeSpan.Zero);
        await Assert.That(visible.Tick()).IsTrue();
        var tooSmall = TestRender.Buffer(24, 3);

        visible.Paint(tooSmall, new Rect(0, 0, 4, 2));

        await Assert.That(ScreenFixtures.Row(tooSmall, 0, 24)).IsEqualTo(new string(' ', 24));
    }
}
