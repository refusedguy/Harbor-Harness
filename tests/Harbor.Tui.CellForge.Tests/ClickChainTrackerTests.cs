using EngineInput = Harbor.Tui.CellForge.Input;

namespace Harbor.Tui.CellForge.Tests;

/// <summary>
/// Issue #2492: the input layer needs one deterministic click-chain counter
/// instead of every consumer inventing its own double-click timer. All times
/// below come from <see cref="ManualTimeProvider"/>: no sleeps, no real clock.
/// </summary>
public sealed class ClickChainTrackerTests
{
    private static EngineInput.MouseEvent Click(
        EngineInput.MouseButton button = EngineInput.MouseButton.Left,
        int column = 4,
        int row = 7) =>
        new(EngineInput.MouseEventType.Click, button, column, row, EngineInput.KeyModifiers.None);

    [Test]
    public async Task Window_IsFiveHundredMilliseconds()
    {
        await Assert.That(EngineInput.ClickChainTracker.Window).IsEqualTo(TimeSpan.FromMilliseconds(500));
    }

    [Test]
    public async Task FirstClick_IsSingle()
    {
        var tracker = new EngineInput.ClickChainTracker(new ManualTimeProvider());

        await Assert.That(tracker.Register(Click())).IsEqualTo(EngineInput.ClickChain.Single);
    }

    [Test]
    public async Task TwoClicksInsideWindow_AreDouble()
    {
        var clock = new ManualTimeProvider();
        var tracker = new EngineInput.ClickChainTracker(clock);

        await Assert.That(tracker.Register(Click())).IsEqualTo(EngineInput.ClickChain.Single);
        clock.AdvanceMs(499);
        await Assert.That(tracker.Register(Click())).IsEqualTo(EngineInput.ClickChain.Double);
    }

    [Test]
    public async Task ThreeClicksInsideWindow_AreTriple_AndStayTriple()
    {
        var clock = new ManualTimeProvider();
        var tracker = new EngineInput.ClickChainTracker(clock);

        _ = tracker.Register(Click());
        _ = tracker.Register(Click());
        clock.AdvanceMs(499);
        await Assert.That(tracker.Register(Click())).IsEqualTo(EngineInput.ClickChain.Triple);
        clock.AdvanceMs(499);
        await Assert.That(tracker.Register(Click())).IsEqualTo(EngineInput.ClickChain.Triple);
    }

    [Test]
    public async Task Click_ExactlyAtWindowBoundary_StartsNewChain()
    {
        var clock = new ManualTimeProvider();
        var tracker = new EngineInput.ClickChainTracker(clock);

        _ = tracker.Register(Click());
        clock.Advance(EngineInput.ClickChainTracker.Window);
        await Assert.That(tracker.Register(Click())).IsEqualTo(EngineInput.ClickChain.Single);

        // The new click is the head of the next chain, not a stale one.
        clock.AdvanceMs(1);
        await Assert.That(tracker.Register(Click())).IsEqualTo(EngineInput.ClickChain.Double);
    }

    [Test]
    public async Task Click_AfterWindow_StartsNewChain()
    {
        var clock = new ManualTimeProvider();
        var tracker = new EngineInput.ClickChainTracker(clock);

        _ = tracker.Register(Click());
        clock.AdvanceMs(501);
        await Assert.That(tracker.Register(Click())).IsEqualTo(EngineInput.ClickChain.Single);
    }

    [Test]
    public async Task Click_OnDifferentCell_StartsNewChain()
    {
        var clock = new ManualTimeProvider();
        var tracker = new EngineInput.ClickChainTracker(clock);

        _ = tracker.Register(Click(column: 4, row: 7));
        clock.AdvanceMs(1);
        await Assert.That(tracker.Register(Click(column: 5, row: 7))).IsEqualTo(EngineInput.ClickChain.Single);
        clock.AdvanceMs(1);
        await Assert.That(tracker.Register(Click(column: 5, row: 7))).IsEqualTo(EngineInput.ClickChain.Double);
    }

    [Test]
    public async Task Click_WithDifferentButton_StartsNewChain()
    {
        var clock = new ManualTimeProvider();
        var tracker = new EngineInput.ClickChainTracker(clock);

        _ = tracker.Register(Click(EngineInput.MouseButton.Left));
        clock.AdvanceMs(1);
        await Assert.That(tracker.Register(Click(EngineInput.MouseButton.Right)))
            .IsEqualTo(EngineInput.ClickChain.Single);
    }

    [Test]
    public async Task Tolerance_AllowsNeighbouringCell_ButNotDistantOne()
    {
        var clock = new ManualTimeProvider();
        var tracker = new EngineInput.ClickChainTracker(clock, tolerance: 1);

        _ = tracker.Register(Click(column: 4, row: 7));
        clock.AdvanceMs(1);
        await Assert.That(tracker.Register(Click(column: 5, row: 8))).IsEqualTo(EngineInput.ClickChain.Double);
        clock.AdvanceMs(1);
        await Assert.That(tracker.Register(Click(column: 7, row: 7))).IsEqualTo(EngineInput.ClickChain.Single);
    }

    [Test]
    public async Task Reset_DropsTheChain()
    {
        var clock = new ManualTimeProvider();
        var tracker = new EngineInput.ClickChainTracker(clock);

        _ = tracker.Register(Click());
        _ = tracker.Register(Click());
        tracker.Reset();
        await Assert.That(tracker.Register(Click())).IsEqualTo(EngineInput.ClickChain.Single);
    }

    [Test]
    public async Task NonClickEvent_IsRejected()
    {
        var tracker = new EngineInput.ClickChainTracker(new ManualTimeProvider());
        var press = new EngineInput.MouseEvent(
            EngineInput.MouseEventType.Press,
            EngineInput.MouseButton.Left,
            4,
            7,
            EngineInput.KeyModifiers.None);

        await Assert.That(() => tracker.Register(press)).Throws<ArgumentException>();
    }
}
