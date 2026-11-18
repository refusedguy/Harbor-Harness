namespace Harbor.TestKit.Tests;

/// <summary>
///     The manual clock. The properties worth pinning are the ones the eight
///     hand-written copies disagreed on: whether the timer fires when it is
///     created, and what the recorded delays are. The deadline-jumping tests
///     pin the contract a test needs when the delay belongs to the component
///     under test rather than to the test author.
/// </summary>
public class ManualTimeProviderTests
{
    [Test]
    public async Task Does_Not_Move_On_Its_Own()
    {
        var clock = new ManualTimeProvider();

        DateTimeOffset first = clock.GetUtcNow();
        await Task.Delay(5);
        DateTimeOffset second = clock.GetUtcNow();

        await Assert.That(second).IsEqualTo(first)
            .Because("a clock the test does not drive is a clock the test cannot make a deadline exact with");
    }

    [Test]
    public async Task Advance_Moves_The_Wall_Clock()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UnixEpoch);

        clock.Advance(TimeSpan.FromSeconds(90));

        await Assert.That(clock.GetUtcNow()).IsEqualTo(DateTimeOffset.UnixEpoch.AddSeconds(90));
    }

    [Test]
    public async Task AdvanceMs_Moves_The_Tick_Clock()
    {
        var clock = new ManualTimeProvider();

        clock.AdvanceMs(250);

        await Assert.That(clock.GetTimestamp()).IsEqualTo(250)
            .Because("one tick is one millisecond, so a duration in ticks and a duration in milliseconds are the same number and every deadline is exact integer arithmetic");
    }

    [Test]
    public async Task The_Tick_Frequency_Is_One_Kilohertz()
    {
        var clock = new ManualTimeProvider();

        await Assert.That(clock.TimestampFrequency).IsEqualTo(1_000);
    }

    [Test]
    public async Task Records_The_Delays_It_Was_Asked_For()
    {
        var clock = new ManualTimeProvider();

        using ITimer timer = clock.CreateTimer(_ => { }, null, TimeSpan.FromMilliseconds(80), TimeSpan.Zero);

        await Assert.That(clock.Delays.Count).IsEqualTo(1);
        await Assert.That(clock.Delays[0]).IsEqualTo(TimeSpan.FromMilliseconds(80));
    }

    [Test]
    public async Task A_Frozen_Timer_Does_Not_Fire_When_It_Is_Created()
    {
        var clock = new ManualTimeProvider();
        bool fired = false;

        using ITimer timer = clock.CreateTimer(_ => fired = true, null, TimeSpan.Zero, TimeSpan.Zero);

        await Assert.That(fired).IsFalse()
            .Because("a grace timer that fires the moment it is created is not a grace timer");
    }

    [Test]
    public async Task A_Recording_Timer_Fires_When_It_Is_Created()
    {
        var clock = new ManualTimeProvider(fireTimerOnCreate: true);
        bool fired = false;

        using ITimer timer = clock.CreateTimer(_ => fired = true, null, TimeSpan.Zero, TimeSpan.Zero);

        await Assert.That(fired).IsTrue()
            .Because("a retry policy that observes a Task.Delay must see an already-elapsed delay, or the backoff it measures is the scheduler's, not the policy's");
    }

    [Test]
    public async Task AdvanceToNextTimer_LandsOnTheDeadlineAndFiresTheTimer()
    {
        var clock = new ManualTimeProvider(fireTimerOnAdvance: true);
        var fired = 0;
        using ITimer timer = clock.CreateTimer(_ => fired++, null, TimeSpan.FromSeconds(30), TimeSpan.Zero);

        bool advanced = clock.AdvanceToNextTimer();

        await Assert.That(advanced).IsTrue()
            .Because("the clock had one armed timer with a deadline to jump to");
        await Assert.That(clock.GetUtcNow()).IsEqualTo(DateTimeOffset.UnixEpoch.AddSeconds(30))
            .Because("the caller never named a delay, so the clock must land exactly on the timer's own deadline");
        await Assert.That(clock.GetTimestamp()).IsEqualTo(30_000)
            .Because("the tick clock must move with the wall clock, or a deadline read from GetTimestamp still needs a magic number");
        await Assert.That(fired).IsEqualTo(1)
            .Because("reaching a deadline is what fires the timer");
        await Assert.That(clock.ActiveTimerCount).IsEqualTo(0)
            .Because("a one-shot timer is disarmed once it has fired");
    }

    [Test]
    public async Task AdvanceToNextTimer_WithNoArmedTimer_DoesNotMoveTheClock()
    {
        var clock = new ManualTimeProvider(fireTimerOnAdvance: true);

        bool advanced = clock.AdvanceToNextTimer();

        await Assert.That(advanced).IsFalse()
            .Because("there was no deadline to jump to");
        await Assert.That(clock.GetUtcNow()).IsEqualTo(DateTimeOffset.UnixEpoch)
            .Because("a clock with nothing to fire is not a clock that should move");
    }

    [Test]
    public async Task AdvanceToNextTimer_VisitsTheEarliestDeadlineFirst()
    {
        var clock = new ManualTimeProvider(fireTimerOnAdvance: true);
        var order = new List<string>();
        using ITimer later = clock.CreateTimer(_ => order.Add("later"), null, TimeSpan.FromSeconds(30), TimeSpan.Zero);
        using ITimer sooner = clock.CreateTimer(_ => order.Add("sooner"), null, TimeSpan.FromSeconds(5), TimeSpan.Zero);

        clock.AdvanceToNextTimer();

        await Assert.That(clock.GetUtcNow()).IsEqualTo(DateTimeOffset.UnixEpoch.AddSeconds(5))
            .Because("the 5-second timer was armed second but its deadline is earlier");
        await Assert.That(order).IsEquivalentTo(new[] { "sooner" })
            .Because("only the timer whose deadline was reached may fire");

        clock.AdvanceToNextTimer();

        await Assert.That(clock.GetUtcNow()).IsEqualTo(DateTimeOffset.UnixEpoch.AddSeconds(30))
            .Because("the second call resumes from the first deadline, not from the start");
        await Assert.That(order).IsEquivalentTo(new[] { "sooner", "later" })
            .Because("each deadline is visited exactly once, in deadline order");
    }

    [Test]
    public async Task AdvanceToNextTimer_OnPeriodicTimer_ConsumesOneDeadlinePerCall()
    {
        var clock = new ManualTimeProvider(fireTimerOnAdvance: true);
        var fired = 0;
        using ITimer timer = clock.CreateTimer(_ => fired++, null, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));

        clock.AdvanceToNextTimer();
        await Assert.That(clock.GetUtcNow()).IsEqualTo(DateTimeOffset.UnixEpoch.AddSeconds(10));
        await Assert.That(fired).IsEqualTo(1);

        clock.AdvanceToNextTimer();

        await Assert.That(clock.GetUtcNow()).IsEqualTo(DateTimeOffset.UnixEpoch.AddSeconds(20))
            .Because("a periodic timer keeps its cadence across calls");
        await Assert.That(fired).IsEqualTo(2)
            .Because("one call consumes one deadline; a second call must not swallow two");
    }
}
